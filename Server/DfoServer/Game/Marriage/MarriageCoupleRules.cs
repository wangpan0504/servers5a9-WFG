using System.Collections.Immutable;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Marriage;

namespace DfoServer.Game.Marriage;

internal readonly record struct MarriageCoupleSlot(byte Container, ushort Slot, int ItemId);
internal readonly record struct MarriageItemDelta(ushort MemberId, int ItemId, int Count, ushort? Slot = null);
internal sealed record MarriageCoupleTransition(MarriageRecord Record, int Result,
    ImmutableArray<MarriageItemDelta> Items)
{
    public bool Success => Result == 0;
}

internal static class MarriageCoupleRules
{
    public const int StorageSlots = 40;
    public const int MaxCommentBytes = 255;
    // 仅绑定CMD1048的原生回调，不能借作其他婚房命令的通用错误表。
    public const int MoveInvalidRequest = -1;
    public const int MoveNoMarriage = -2;
    public const int MoveStorageFull = -13;
    public const int MoveEditorBusy = -20;
    public const int MoveInvalidPlacement = -22;

    public static MarriageCoupleTransition Move(MarriageRecord record, ushort member,
        MarriageCoupleSlot source, MarriageCoupleSlot target, MarriageCatalog catalog)
    {
        if (!Member(record, member)) return Fail(record, MoveNoMarriage);
        if (!ValidSlot(source, catalog) || !ValidSlot(target, catalog) ||
            source.Container == target.Container && source.Slot == target.Slot) return Fail(record, MoveInvalidRequest);
        var from = source.Container == 0 ? record.Furniture : record.Storage;
        var to = target.Container == 0 ? record.Furniture : record.Storage;
        if (!from.TryGetValue(source.Slot, out var value) || value.ItemId != source.ItemId || value.Count <= 0)
            return Fail(record, MoveInvalidRequest);
        bool occupied = to.TryGetValue(target.Slot, out var replaced);
        // 原生摆放、收回在两端都写入源家具编号；只有同容器拖动声明目标原物品。
        int targetItem = source.Container != target.Container ? source.ItemId : occupied ? replaced.ItemId : 0;
        if (target.ItemId != targetItem) return Fail(record, MoveInvalidRequest);
        var furniture = record.Furniture;
        var storage = record.Storage;
        if (source.Container == 1 && target.Container == 0 && value.Count > 1)
        {
            if (occupied || !Fits(value with { Count = 1 }, target, catalog)) return Fail(record, MoveInvalidPlacement);
            Put(source, value with { Count = value.Count - 1 });
            Put(target, value with { Count = 1 });
            return Done(record with { Furniture = furniture, Storage = storage });
        }
        if (source.Container == 0 && target.Container == 1 && occupied && replaced.ItemId == value.ItemId)
        {
            if ((long)replaced.Count + value.Count > int.MaxValue) return Fail(record, MoveStorageFull);
            Put(source, null);
            Put(target, replaced with { Count = replaced.Count + value.Count });
            return Done(record with { Furniture = furniture, Storage = storage });
        }
        if (!Fits(value, target, catalog)) return Fail(record, MoveInvalidPlacement);
        if (occupied && !Fits(replaced, source, catalog))
            return Fail(record, source.Container == 0 && target.Container == 1 ? MoveStorageFull : MoveInvalidPlacement);
        Put(source, occupied ? replaced : null);
        Put(target, value);
        return Done(record with { Furniture = furniture, Storage = storage });

        void Put(MarriageCoupleSlot slot, MarriageFurnitureStack? stack)
        {
            if (slot.Container == 0)
                furniture = stack is { } item ? furniture.SetItem(slot.Slot, item) : furniture.Remove(slot.Slot);
            else
                storage = stack is { } item ? storage.SetItem(slot.Slot, item) : storage.Remove(slot.Slot);
        }
    }

    public static MarriageCoupleTransition Store(MarriageRecord record, ushort member, int itemId, int count,
        ushort inventorySlot, MarriageCatalog catalog)
    {
        if (!Member(record, member)) return Fail(record, -2);
        if (count <= 0 || !catalog.Furniture.ContainsKey(itemId)) return Fail(record, -3);
        ushort? free = null;
        for (ushort slot = 0; slot < StorageSlots; slot++)
            if (!record.Storage.ContainsKey(slot)) { free = slot; break; }
        if (free is null) return Fail(record, -4);
        return Done(record with { Storage = record.Storage.Add(free.Value, new(itemId, count)) },
            new MarriageItemDelta(member, itemId, -count, inventorySlot));
    }

    public static MarriageCoupleTransition Delete(MarriageRecord record, ushort member, ushort slot, int itemId, int count)
    {
        if (!Member(record, member)) return Fail(record, -2);
        if (slot >= StorageSlots || count <= 0 || !record.Storage.TryGetValue(slot, out var item) ||
            item.ItemId != itemId || item.Count < count) return Fail(record, -3);
        var storage = item.Count == count ? record.Storage.Remove(slot) :
            record.Storage.SetItem(slot, item with { Count = item.Count - count });
        return Done(record with { Storage = storage });
    }

    public static MarriageCoupleTransition Plant(MarriageRecord record, ushort member, ushort slot, int seed,
        long now, int day, MarriageCatalog catalog)
    {
        if (!Member(record, member)) return Fail(record, -2);
        if (now < 0 || day < 0 || !catalog.Seeds.TryGetValue(seed, out int index)) return Fail(record, -1);
        if (record.Tree is { }) return Fail(record, -4);
        var definition = catalog.Trees[index];
        long duration = (long)definition.ExpireDays * 86400;
        if (now > uint.MaxValue - duration) return Fail(record, -1);
        return Done(record with { Tree = new() { Seed = seed, Expiry = now + duration, WaterDay = day } },
            new MarriageItemDelta(member, seed, -1, slot));
    }

    public static MarriageCoupleTransition Water(MarriageRecord record, ushort member, ushort slot, int itemId,
        long now, int day, MarriageCatalog catalog)
    {
        if (!Member(record, member)) return Fail(record, -2);
        if (record.Tree is not { } tree) return Fail(record, -4);
        if (now >= tree.Expiry) return Fail(record, -11);
        if (now < 0 || day < tree.WaterDay || !Tree(record, catalog, out var definition)) return Fail(record, -1);
        if (!definition.GrowPoints.TryGetValue(itemId, out int points)) return Fail(record, -1);
        int waters = day > tree.WaterDay ? 0 : tree.Waters;
        if (waters >= definition.DayWaterCount) return Fail(record, -6);
        int maximum = definition.Steps[^1].GrowPoint;
        if (tree.Growth >= maximum) return Fail(record, -9);
        int growth = (int)Math.Min(maximum, (long)tree.Growth + points);
        return Done(record with { Tree = tree with { Growth = growth, WaterDay = day, Waters = waters + 1 } },
            new MarriageItemDelta(member, itemId, -1, slot));
    }

    public static MarriageCoupleTransition Harvest(MarriageRecord record, ushort member, long now,
        MarriageCatalog catalog, Func<long, long> draw)
    {
        if (!Member(record, member)) return Fail(record, -2);
        if (record.Tree is not { } tree) return Fail(record, -4);
        if (now >= tree.Expiry) return Fail(record, -7);
        if (now < 0 || !Tree(record, catalog, out var definition)) return Fail(record, -1);
        if (tree.Growth < definition.Steps[^1].GrowPoint) return Fail(record, -6);
        long total = definition.TotalRewardWeight;
        long selected = draw(total);
        if (selected < 0 || selected >= total) throw new ArgumentOutOfRangeException(nameof(draw));
        // 相同物品的不同数量仍是独立权重行，不合并抽样区间。
        foreach (var row in definition.Rewards)
        {
            if (selected < row.Weight)
                return Done(record with { Tree = null }, new MarriageItemDelta(member, row.ItemId, row.Count));
            selected -= row.Weight;
        }
        throw new InvalidOperationException("夫妻树奖励权重不完整");
    }

    public static MarriageCoupleTransition RemoveTree(MarriageRecord record, ushort member)
    {
        if (!Member(record, member)) return Fail(record, -2);
        return record.Tree is null ? Fail(record, -4) : Done(record with { Tree = null });
    }

    // 推荐地下城、BOSS完成等资格由已有副本结算上下文验证，本规则只接受已冻结的有效事件。
    public static MarriageCoupleTransition ObserveQualifiedClear(MarriageRecord record, ushort member, Guid eventId,
        int day, MarriageCatalog catalog)
    {
        if (!TryMember(record, member, day, out var state) || eventId == Guid.Empty) return Fail(record, -2);
        if (record.ClearEvents.Contains(eventId)) return Fail(record, -3);
        state = state with { CapsuleGauge = (int)Math.Min(catalog.GrowthCapsule.MaxGage,
            (long)state.CapsuleGauge + catalog.GrowthCapsule.Gage) };
        return Done(record with { Members = record.Members.SetItem(member, state), ClearEvents = record.ClearEvents.Add(eventId) });
    }

    public static MarriageCoupleTransition MakeCapsule(MarriageRecord record, ushort member, int day, MarriageCatalog catalog)
    {
        if (!TryMember(record, member, day, out var state)) return Fail(record, -2);
        var definition = catalog.GrowthCapsule;
        if (state.CapsuleGauge < definition.MaxGage) return Fail(record, -4);
        if (state.CapsulesOwned >= definition.MaxOwnCount) return Fail(record, -5);
        state = state with { CapsuleGauge = state.CapsuleGauge - definition.MaxGage, CapsulesOwned = state.CapsulesOwned + 1 };
        return Done(record with { Members = record.Members.SetItem(member, state) });
    }

    public static MarriageCoupleTransition GiveCapsule(MarriageRecord record, ushort member, int day)
    {
        if (!TryMember(record, member, day, out var state) ||
            !record.Members.TryGetValue(record.Other(member), out var partner)) return Fail(record, -2);
        if (state.CapsulesOwned <= 0) return Fail(record, -4);
        if (partner.CapsulesPending == int.MaxValue) return Fail(record, -6);
        return Done(record with { Members = record.Members
            .SetItem(member, state with { CapsulesOwned = state.CapsulesOwned - 1 })
            .SetItem(record.Other(member), partner with { CapsulesPending = partner.CapsulesPending + 1 }) });
    }

    public static MarriageCoupleTransition TakeCapsule(MarriageRecord record, ushort member, int day, MarriageCatalog catalog)
    {
        if (!TryMember(record, member, day, out var state)) return Fail(record, -2);
        // 本版-4对应不相关的礼堂确认文案；无待领项使用原生通用关联失败，不冒用该分支。
        if (state.CapsulesPending <= 0) return Fail(record, -1);
        if (state.CapsulesTaken >= catalog.GrowthCapsule.MaxTakenCount) return Fail(record, -7);
        state = state with { CapsulesPending = state.CapsulesPending - 1, CapsulesTaken = state.CapsulesTaken + 1 };
        return Done(record with { Members = record.Members.SetItem(member, state) }, new MarriageItemDelta(member, catalog.GrowthCapsule.CapsuleId, 1));
    }

    public static MarriageCoupleTransition AddComment(MarriageRecord record, ushort author, string name, string text,
        uint id, long now, int maxComments)
    {
        if (!record.Active || author == 0 || id == 0 || now < 0 || now > uint.MaxValue ||
            maxComments <= 0 || record.Comments.Length >= maxComments || record.Comments.Any(row => row.Id == id) ||
            !Text(name, 29) || !Text(text, MaxCommentBytes)) return Fail(record, -3);
        return Done(record with { Comments = record.Comments.Add(new(id, author, name, now, text)) });
    }

    public static MarriageCoupleTransition DeleteComment(MarriageRecord record, ushort member, uint id)
    {
        if (!record.Active || !record.IsMember(member)) return Fail(record, -2);
        int index = -1;
        for (int at = 0; at < record.Comments.Length; at++)
            if (record.Comments[at].Id == id) { index = at; break; }
        return index < 0 ? Fail(record, -3) : Done(record with { Comments = record.Comments.RemoveAt(index) });
    }

    static bool Member(MarriageRecord record, ushort member) => record.State == 4 && record.IsMember(member);
    static bool TryMember(MarriageRecord record, ushort member, int day, out MarriageMemberState state)
    {
        state = new();
        if (!Member(record, member) || day < 0 || !record.Members.TryGetValue(member, out var value) || day < value.CapsuleDay)
            return false;
        // 持有数量与待领取数量持久保留；只有领取额度按单调日期重置。
        state = day > value.CapsuleDay ? value with { CapsuleDay = day, CapsulesTaken = 0 } : value;
        return true;
    }

    static bool Tree(MarriageRecord record, MarriageCatalog catalog, out MarriageTreeDefinition definition)
    {
        definition = null!;
        return record.Tree is { } tree && catalog.Seeds.TryGetValue(tree.Seed, out int index) &&
            catalog.Trees.TryGetValue(index, out definition!);
    }

    static bool ValidSlot(MarriageCoupleSlot slot, MarriageCatalog catalog) => slot.Container switch
    {
        0 => catalog.RoomPositions.ContainsKey(slot.Slot),
        1 => slot.Slot < StorageSlots,
        _ => false
    };

    static bool Fits(MarriageFurnitureStack value, MarriageCoupleSlot target, MarriageCatalog catalog) =>
        value.Count > 0 && catalog.Furniture.TryGetValue(value.ItemId, out var definition) &&
        (target.Container == 1 || value.Count == 1 && definition.Placements.Any(row => row.PositionId == target.Slot));

    static bool Text(string text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\0') || text.Any(char.IsControl)) return false;
        try { return CharacterName.Encoding.GetByteCount(text) <= limit; }
        catch (EncoderFallbackException) { return false; }
    }

    static MarriageCoupleTransition Fail(MarriageRecord record, int result) => new(record, result, []);
    static MarriageCoupleTransition Done(MarriageRecord record, params MarriageItemDelta[] items) =>
        new(record with { Revision = checked(record.Revision + 1) }, 0, [.. items]);
}
