using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryTalismanService(ItemCatalog items, Func<int, int>? random = null)
{
    public const ushort Rejection = 23;
    public const ushort InventoryFull = 95;

    public InventoryResult Equip(Character character, RuneEquipRequest request, long utcSeconds)
    {
        if (request.TargetSlot is < 210 or > 215 || request.SourceSlot is < 70 or > 209 || request.ItemId <= 0)
            return InventoryResult.Reject(Rejection);
        var before = character.Inventory;
        var target = new InventorySlot(InventoryListType.Talisman, (short)request.TargetSlot);
        var sourceSlot = new InventorySlot(InventoryListType.Talisman, (short)request.SourceSlot);
        var source = before.Find(sourceSlot);
        var talisman = before.Find(Parent(target.Index));
        var previous = before.Find(target);
        if (!Usable(source, ItemKind.Rune, utcSeconds) || source!.Core.ItemId != request.ItemId ||
            !Usable(talisman, ItemKind.Talisman, utcSeconds) ||
            previous is not null && !Usable(previous, ItemKind.Rune, utcSeconds))
            return InventoryResult.Reject(Rejection);
        var rune = items.Rune(source.Core.ItemId);
        var talismanDefinition = items.Talisman(talisman!.Core.ItemId);
        if (rune?.AllowsUse(character.Level, character.Job, character.GrowType) != true ||
            talismanDefinition?.AllowsUse(character.Level, character.Job, character.GrowType) != true)
            return InventoryResult.Reject(Rejection);
        // 孔色是唯一限制：符文阵营颜色与目标孔色一致即可装入，不要求技能与护石一致，同孔色可重复同阵营符文。
        if (TalismanSocketColors.Read(talisman.Core)[(target.Index - 210) % 3] != rune.SocketColor)
            return InventoryResult.Reject(Rejection);
        var planned = before.Items.ToDictionary(item => item.Position);
        planned.Remove(target);
        if (source.Core.Count == 1)
            planned.Remove(sourceSlot);
        else
            planned[sourceSlot] = source with { Core = source.Core with { Value = source.Core.Count - 1 } };
        if (previous is not null && !ReturnRune(planned, previous, before))
            return InventoryResult.Reject(InventoryFull);
        planned[target] = source with
        {
            Uid = source.Core.Count == 1 ? source.Uid : 0,
            Position = target,
            Core = source.Core with { Value = 1, SortLockFlag = 0 }
        };
        return Finish(character, planned, "equip_rune", Deadline(source, talisman, previous));
    }

    public InventoryResult Remove(Character character, ushort targetSlot, long utcSeconds)
    {
        if (targetSlot is < 210 or > 215)
            return InventoryResult.Reject(Rejection);
        var before = character.Inventory;
        var target = new InventorySlot(InventoryListType.Talisman, (short)targetSlot);
        var source = before.Find(target);
        var parent = before.Find(Parent(target.Index));
        if (!Usable(source, ItemKind.Rune, utcSeconds) || parent?.Core.EquipmentLockId > 0)
            return InventoryResult.Reject(Rejection);
        var planned = before.Items.ToDictionary(item => item.Position);
        planned.Remove(target);
        if (!ReturnRune(planned, source!, before))
            return InventoryResult.Reject(InventoryFull);
        return Finish(character, planned, "remove_rune", Deadline(source));
    }

    public InventoryResult Refine(Character character, TalismanRefineRequest request, TalismanRefineCost costs,
        long utcSeconds)
    {
        if (request.TalismanSlot is < 0 or > 69)
            return InventoryResult.Reject(Rejection);
        var before = character.Inventory;
        var target = before.Find(new(InventoryListType.Talisman, request.TalismanSlot));
        var material = before.Find(new(InventoryListType.Main, request.MaterialSlot));
        if (!Usable(target, ItemKind.Talisman, utcSeconds) ||
            !costs.CountsByRarity.TryGetValue(items.Get(target!.Core.ItemId).Rarity, out int count) ||
            material is null || material.Core.ItemId != costs.MaterialId || !material.Core.IsStackable ||
            material.Core.Count < count || material.Core.EquipmentLockId != 0 || material.IsExpired(utcSeconds))
            return InventoryResult.Reject(Rejection);
        var consumed = InventoryDeleteService.Plan(before, material.Position, material.Core.ItemId, count);
        if (!consumed.Accepted)
            return InventoryResult.Reject(Rejection);
        var changed = target with { Core = TalismanSocketPolicy.Generate(random).Apply(target.Core) };
        var after = consumed.Plan!.After with { Items = consumed.Plan.After.Items.Replace(target, changed) };
        after.Validate(allocating: true);
        return new(true, 0, new(before, after, "talisman_refine", new(character, Deadline(target, material))));
    }

    internal bool ReturnRunes(Dictionary<InventorySlot, InventoryItem> planned,
        IReadOnlyList<InventoryItem> runes, InventorySnapshot before, long utcSeconds, out long? earliestExpireTime)
    {
        earliestExpireTime = null;
        foreach (var rune in runes)
        {
            if (!Usable(rune, ItemKind.Rune, utcSeconds))
                return false;
            if (rune.ExpireTime > 0)
                earliestExpireTime = earliestExpireTime is null ? rune.ExpireTime : Math.Min(earliestExpireTime.Value, rune.ExpireTime);
            planned.Remove(rune.Position);
            if (!ReturnRune(planned, rune, before))
                return false;
        }
        return true;
    }

    bool ReturnRune(Dictionary<InventorySlot, InventoryItem> planned, InventoryItem rune, InventorySnapshot before)
    {
        int limit = items.Get(rune.Core.ItemId).StackLimit;
        var merge = planned.Values.Where(item => item.Position.List == InventoryListType.Talisman &&
            item.Position.Index is >= 70 and <= 209 && InventoryStackRuleService.CanMerge(rune.Core, item.Core, 1, limit))
            .MinBy(item => item.Position.Index);
        if (merge is not null)
        {
            planned[merge.Position] = merge with { Core = merge.Core with { Value = merge.Core.Count + 1 } };
            return true;
        }
        var (start, end) = ItemSlotBoundService.Range(ItemKind.Rune, before);
        for (short slot = start; slot <= end; slot++)
        {
            var position = new InventorySlot(InventoryListType.Talisman, slot);
            if (planned.ContainsKey(position))
                continue;
            planned.Add(position, rune with { Position = position });
            return true;
        }
        return false;
    }

    static InventorySlot Parent(short target) => new(InventoryListType.Equipment, (short)(34 + (target - 210) / 3));
    static bool Usable(InventoryItem? item, ItemKind kind, long utcSeconds) => item is not null &&
        item.Core.Kind == kind && item.Core.Count > 0 && item.Core.EquipmentLockId == 0 && !item.IsExpired(utcSeconds);

    static long? Deadline(params InventoryItem?[] items) => items.Where(item => item is { ExpireTime: > 0 })
        .Select(item => (long?)item!.ExpireTime).Min();

    static InventoryResult Finish(Character character, Dictionary<InventorySlot, InventoryItem> planned, string reason,
        long? deadline)
    {
        var after = character.Inventory with { Items = planned.Values.OrderBy(item => item.Position.List)
            .ThenBy(item => item.Position.Index).ToImmutableArray() };
        after.Validate(allocating: true);
        return new(true, 0, new(character.Inventory, after, reason, new(character, deadline)));
    }
}
