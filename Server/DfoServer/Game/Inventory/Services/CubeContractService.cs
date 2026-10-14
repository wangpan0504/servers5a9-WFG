using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class CubeContractService(PremiumContractCatalog contracts)
{
    public InventoryResult Select(Character character, CubeContractState request, long now)
    {
        request.Validate();
        var before = character.Inventory;
        if (request.CubeType >= 0 && (!Active(before, now) || !contracts.HasStackable(contracts.Cubes[request.CubeType].ItemId)))
            return InventoryResult.Reject();
        var after = before with { CubeContract = request };
        return new(true, 0, new(before, after, "cube_contract_select", new(character)));
    }

    public CubeContractState ForClient(InventorySnapshot inventory, long now)
    {
        if (!Active(inventory, now) || inventory.CubeContract.CubeType >= 0 &&
            !contracts.HasStackable(contracts.Cubes[inventory.CubeContract.CubeType].ItemId)) return CubeContractState.Empty;
        return inventory.CubeContract;
    }

    public CubeContractOption? Consumption(InventorySnapshot inventory, long now)
    {
        var choice = ForClient(inventory, now);
        if (choice.CubeType < 0) return null;
        var option = contracts.Cubes[choice.CubeType];
        return CurrencyWallet.RewardKind(option.ItemId) is { } kind &&
            inventory.AccountWallet.Get(kind) >= option.ConsumeCount ? option : null;
    }

    public InventoryResult Consume(Character character, sbyte expectedType, long now)
    {
        if (character.Inventory.CubeContract.CubeType != expectedType || Consumption(character.Inventory, now) is not { } option ||
            CurrencyWallet.RewardKind(option.ItemId) is not { } kind) return InventoryResult.Reject();
        var spent = CurrencyService.Spend(character.Inventory, kind, option.ConsumeCount);
        return spent.Accepted ? new(true, 0, new(character.Inventory, spent.Plan!.After, "cube_contract_consume", new(character))) : spent;
    }

    bool Active(InventorySnapshot inventory, long now) => contracts.Cubes.Length == 6 && inventory.AccountWallet.ContractExpiry(97) > now;
}
