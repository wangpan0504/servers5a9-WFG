using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryVendingService(VendingMachineCatalog machines, ItemCatalog items, GoldLimitCatalog limits,
    EpicPieceCatalog pieces, PremiumContractCatalog contracts, TimeProvider? clock = null, Random? random = null)
{
    public VendingMachine? Machine(int id) => machines.Find(id);

    public InventoryResult Open(Character character, VendingRequest request, out VendingReceipt? receipt)
    {
        receipt = null;
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var machine = machines.Find(request.Machine);
        var slot = new InventorySlot(InventoryListType.Main, request.CoinSlot);
        var coin = character.Inventory.Find(slot);
        if (machine is null || request.Group != machine.Group || request.CoinSlot < 0 ||
            character.BlackDiamondExpiresAt <= now.ToUnixTimeSeconds() || coin is null ||
            coin.Core.ItemId != machine.CoinId || !coin.Core.IsStackable || coin.Core.Count < machine.CoinCount ||
            coin.Core.EquipmentLockId != 0 || coin.Core.IsExpired(now.ToUnixTimeSeconds()))
            return InventoryResult.Reject(1);

        long draw = (random ?? Random.Shared).NextInt64(machine.TotalWeight);
        var prize = machine.Prizes.First(row => (draw -= row.Weight) < 0);
        var paid = InventoryDeleteService.Plan(character.Inventory, slot, coin.Core.Value, machine.CoinCount);
        if (!paid.Accepted) return InventoryResult.Reject(1);
        var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid.Plan!.After },
            [new(prize.ItemId, prize.Count)], items, limits, pieces, now, contracts: contracts);
        if (!grant.Accepted) return InventoryResult.Reject(grant.Error == 19 ? (ushort)1 : (ushort)4);
        var plan = grant.Plan!;
        bool contract = contracts.TryAcquisitionContract(prize.ItemId, out _);
        var currency = CurrencyWallet.RewardKind(prize.ItemId);
        if (currency is { } kind && CurrencyWallet.Get(plan.After, kind) - CurrencyWallet.Get(paid.Plan.After, kind) != prize.Count)
            return InventoryResult.Reject(4);
        short? currencySlot = currency is { } rewardKind ? ItemSlotBoundService.CurrencySlot(rewardKind) : null;
        var rows = plan.After.Items.Where(item => item.Core.ItemId == prize.ItemId)
            .Select(item => new VendingGrantedItem(item.Position, item.Core.Count -
                (plan.BeforeIndex.Find(item.Position) is { } old && old.Core.ItemId == prize.ItemId ? old.Core.Count : 0)))
            .Where(row => row.Count > 0).ToImmutableArray();
        if (!contract && currencySlot is null && rows.Sum(row => row.Count) != prize.Count)
            return InventoryResult.Reject(1);
        receipt = new(request.CoinSlot, coin.Core.Count - machine.CoinCount, prize.ItemId, prize.Count, contract, rows, currencySlot);
        long deadline = coin.Core.ExpireTime > 0 ? Math.Min(character.BlackDiamondExpiresAt, coin.Core.ExpireTime) : character.BlackDiamondExpiresAt;
        return grant with { Plan = new(character.Inventory, plan.After, "vending_machine",
            new InventoryCharacterPrecondition(character, deadline, clock, requirePosition: true)) };
    }
}
