using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Titles;

internal sealed class TitleBookService(TitleCatalog titles, ItemCatalog items, PremiumContractCatalog? contracts = null)
{
    public static bool IsBook(InventoryListType list) => list is >= InventoryListType.TitleGeneral and <= InventoryListType.TitleEvent;
    public static InventorySlot Slot(int category, int index) => new((InventoryListType)(18 + category), checked((short)index));

    public ushort CloneTitleError(Character character, uint itemId, long utcSeconds)
        => CloneTitleQualification(character, itemId, utcSeconds).Error;

    public (ushort Error, long? ExpiresAt) CloneTitleQualification(Character character, uint itemId, long utcSeconds)
    {
        if (itemId == 0) return (0, null);
        if (itemId > int.MaxValue) return (2, null);
        int id = (int)itemId;
        if (character.Inventory.Find(new(InventoryListType.Equipment, 13)) is { } worn &&
            items.TryGet(worn.Core.ItemId, out var wornTemplate) && wornTemplate.CannotBeHiddenByCloneTitle)
            return (2, null);
        if (!items.TryGet(id, out var sourceTemplate) || sourceTemplate.EquipmentSlot != 13 || sourceTemplate.CannotBeCloneTitle)
            return (2, null);
        // 动画只借用当前角色真实持有的称号，不能凭客户端模板ID解锁未持有外观。
        var sources = character.Inventory.Items.Where(item => item.Core.ItemId == id &&
            (IsBook(item.Position.List) || item.Position.List == InventoryListType.Main ||
                item.Position is { List: InventoryListType.Equipment, Index: 13 }) &&
            Valid(item, id, utcSeconds)).ToArray();
        if (sources.Length == 0) return (2, null);
        return (0, sources.Any(item => item.ExpireTime <= 0) ? null : sources.Max(item => (long)item.ExpireTime));
    }

    public InventoryResult Put(Character character, TitleBookRequest request, long utcSeconds)
    {
        if (request.List is not (0 or 3) || request.Slot is < 0 or > short.MaxValue ||
            !titles.Fits(request.Category, request.Index, request.ItemId)) return InventoryResult.Reject(2);
        if (character.Level < 10) return InventoryResult.Reject(4);
        var source = new InventorySlot((InventoryListType)request.List, (short)request.Slot);
        var target = Slot(request.Category, request.Index);
        var before = character.Inventory;
        var item = before.Find(source);
        var previous = before.Find(target);
        if (!Valid(item, request.ItemId, utcSeconds) || source.List == InventoryListType.Equipment && source.Index != 13)
            return InventoryResult.Reject(2);
        if (item!.Core.EquipmentLockId != 0 || previous?.Core.EquipmentLockId != 0 && previous is not null)
            return InventoryResult.Reject(1);
        if (request.Category == 3 && item.Core.SealFlag != 0) return InventoryResult.Reject(1);
        if (previous is not null && (!Valid(previous, previous.Core.ItemId, utcSeconds) ||
            !InventoryMoveService.Allows(character, source, previous.Core, items, utcSeconds, contracts))) return InventoryResult.Reject(6);
        // 同格交换也会把旧称号实际穿戴；只沿普通装备的[sealing]消封规则变更该字段。
        if (source.List == InventoryListType.Equipment && previous is not null && previous.Core.SealFlag != 0 &&
            items.Get(previous.Core.ItemId).AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase))
            previous = previous with { Core = previous.Core with { SealFlag = 0 } };
        return Move(character, item, target, previous, source, "title_book_put",
            source.List == InventoryListType.Equipment && previous is not null ? QualificationExpiry(character, previous) : null);
    }

    public InventoryResult Get(Character character, TitleBookRequest request, long utcSeconds)
    {
        if (request.List is not (0 or 3) || request.Slot is < 0 or > short.MaxValue ||
            !titles.Fits(request.Category, request.Index, request.ItemId)) return InventoryResult.Reject(2);
        if (character.Level < 10) return InventoryResult.Reject(4);
        var source = Slot(request.Category, request.Index);
        var before = character.Inventory;
        var item = before.Find(source);
        if (!Valid(item, request.ItemId, utcSeconds)) return InventoryResult.Reject(2);
        if (item!.Core.EquipmentLockId != 0) return InventoryResult.Reject(1);
        if (request.List == 0)
        {
            // 有成就归属的称号只能穿戴；自由展示槽才允许取回背包。
            if (titles.Slots[(request.Category, request.Index)].QuestId != -1) return InventoryResult.Reject(5);
            var target = new InventorySlot(InventoryListType.Main, (short)request.Slot);
            if (!ItemSlotBoundService.Allows(before, target, ItemKind.Equipment) || before.Find(target) is not null)
                return InventoryResult.Reject(11);
            return Move(character, item, target, null, source, "title_book_get");
        }
        if (request.Slot != 13) return InventoryResult.Reject(7);
        var worn = new InventorySlot(InventoryListType.Equipment, 13);
        if (!InventoryMoveService.Allows(character, worn, item.Core, items, utcSeconds, contracts)) return InventoryResult.Reject(6);
        var old = before.Find(worn);
        if (old?.Core.EquipmentLockId != 0 && old is not null) return InventoryResult.Reject(1);
        // 穿戴替换的旧称号回背包装备页，不复制一份永久称号。
        InventorySlot empty = default;
        if (old is not null)
        {
            var range = ItemSlotBoundService.Range(ItemKind.Equipment, before.Expansion);
            empty = Enumerable.Range(range.Start, range.End - range.Start + 1)
                .Select(index => new InventorySlot(InventoryListType.Main, (short)index)).FirstOrDefault(slot => before.Find(slot) is null);
            if (empty == default) return InventoryResult.Reject(11);
        }
        return Move(character, item with { Core = item.Core with { SealFlag = 0 } }, worn, old, empty, "title_book_equip",
            QualificationExpiry(character, item));
    }

    bool Valid(InventoryItem? item, int id, long now) => item is { Core.Kind: ItemKind.Equipment, Core.IsRental: false } &&
        item.Core.ItemId == id && !item.IsExpired(now) && items.Get(id).EquipmentSlot == 13;

    long? QualificationExpiry(Character character, InventoryItem item) => character.Level < items.Get(item.Core.ItemId).MinimumLevel
        ? character.Inventory.AccountWallet.ContractExpiry(22) : null;

    InventoryResult Move(Character character, InventoryItem item, InventorySlot target, InventoryItem? previous,
        InventorySlot returnSlot, string reason, long? qualificationExpiresAt = null)
    {
        var before = character.Inventory;
        var changed = before.Items.Where(value => value.Position != item.Position && value.Position != target).ToList();
        changed.Add(item with { Position = target });
        if (previous is not null) changed.Add(previous with { Position = returnSlot });
        var after = before with { Items = changed.OrderBy(value => value.Position.List).ThenBy(value => value.Position.Index).ToImmutableArray() };
        after.Validate(allocating: true);
        var titleSlot = new InventorySlot(InventoryListType.Equipment, 13);
        bool titleSlotChanged = item.Position == titleSlot || target == titleSlot || previous is not null && returnSlot == titleSlot;
        return new(true, 0, new(before, after, reason, new(character, qualificationExpiresAt))
        {
            CloneTitleItemIdAfter = titleSlotChanged ? InventoryCloneAppearanceService.CloneTitleAfter(character, after, items) : null
        });
    }
}
