using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Inventory.Services;

internal readonly record struct NpcFavorColor(byte Index, byte Value);
internal sealed record NpcFavorRequest(byte Operation, int NpcId, byte SourceType = 0, ushort SourceIndex = 0,
    ImmutableArray<NpcFavorColor> Colors = default);

internal sealed class NpcFavorService(NpcFavorCatalog npcs, TownCatalog? towns = null,
    TimeProvider? time = null, Random? random = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly Random draw = random ?? Random.Shared;
    static readonly int[] CubeItems = [3033, 3034, 3035, 3036, 3037, 3262];

    public InventoryResult Plan(Character character, NpcFavorRequest request)
    {
        var now = clock.GetUtcNow();
        var before = character.Inventory;
        if (npcs.Rules is not { } rules || npcs.Get(request.NpcId) is not { } target ||
            character.Level < rules.MinimumLevel || !Present(character, target.OwnerId)) return InventoryResult.Reject(19);
        var row = before.NpcFavor.SingleOrDefault(value => value.NpcId == target.OwnerId);
        int day = ShopPurchaseCount.Today(now);
        if (request.Operation == 1)
        {
            if (row is null || row.Points < target.Intimate || request.Colors.IsDefaultOrEmpty || request.Colors.Length > 4 ||
                request.Colors.Select(color => color.Index).Distinct().Count() != request.Colors.Length ||
                request.Colors.Any(color => color.Index > 3 || !rules.Colors.Contains(color.Value)))
                return InventoryResult.Reject(19);
            var updated = row;
            foreach (var color in request.Colors) updated = updated.WithColor(color.Index, color.Value);
            return new(true, 0, new(before, Replace(before, row, updated), "npc_favor_color", new(character, requirePosition: true)));
        }
        if (row is { } saved && (saved.Points >= target.Maximum || saved.GiftDay > day || saved.EffectiveGifts(day) >= rules.GiftLimit) ||
            row is null && before.NpcFavor.Length >= byte.MaxValue) return InventoryResult.Reject(19);
        InventoryResult paid;
        NpcFavorGift? gift;
        long deadline = DailyBoosterUsage.NextReset(now).ToUnixTimeSeconds();
        if (request.Operation == 0 && request.SourceType == 36 && request.SourceIndex is >= 354 and <= 359)
        {
            int id = CubeItems[request.SourceIndex - 354];
            if (!rules.Gifts.TryGetValue(id, out gift) || CurrencyWallet.RewardKind(id) is not { } currency)
                return InventoryResult.Reject(19);
            paid = CurrencyService.Spend(before, currency, gift.Count);
        }
        else if (request.Operation == 2 && request.SourceType == 0 && request.SourceIndex <= short.MaxValue)
        {
            var slot = new InventorySlot(InventoryListType.Main, (short)request.SourceIndex);
            var source = before.Find(slot);
            gift = source is null ? null : rules.Extra(target.OwnerId, source.Core.ItemId);
            if (gift is null || source is not { Uid: > 0, Core.Kind: ItemKind.Consumable or ItemKind.Material or ItemKind.Quest or ItemKind.Profession } ||
                source.IsExpired(now.ToUnixTimeSeconds()) || source.Core.Count < gift.Count || source.Core.EquipmentLockId != 0)
                return InventoryResult.Reject(19);
            if (source.ExpireTime > 0) deadline = Math.Min(deadline, source.ExpireTime);
            paid = InventoryDeleteService.Plan(before, slot, source.Core.ItemId, gift.Count);
        }
        else return InventoryResult.Reject(19);
        if (!paid.Accepted) return InventoryResult.Reject(22);
        int points = row?.Points ?? 0;
        int gain = Math.Min(target.Maximum - points, draw.Next(gift.Minimum, gift.Maximum + 1));
        var afterRow = (row ?? new(target.OwnerId, 0)) with
        {
            Points = points + gain, GiftDay = day, GiftCount = checked((byte)((row?.EffectiveGifts(day) ?? 0) + 1))
        };
        return new(true, 0, new(before, Replace(paid.Plan!.After, row, afterRow), "npc_favor_gift",
            new(character, deadline, clock, requirePosition: true)));
    }

    bool Present(Character character, int ownerId) => towns is null ||
        towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) &&
        area.Map(QuestEligibility.Completed(character)).Npcs.Any(npc => npc.Id <= int.MaxValue && npcs.Get((int)npc.Id)?.OwnerId == ownerId);

    static InventorySnapshot Replace(InventorySnapshot inventory, NpcFavorPoint? previous, NpcFavorPoint updated) =>
        inventory with { NpcFavor = (previous is null ? inventory.NpcFavor.Add(updated) : inventory.NpcFavor.Replace(previous, updated))
            .OrderBy(row => row.NpcId).ToImmutableArray() };
}
