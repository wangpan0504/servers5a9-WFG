using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class InventoryCloneAppearanceService
{
    // 只解除借用的外观和染色，期限、能力、徽章及物品实例均保留。
    internal static AvatarDetail Clear(AvatarDetail avatar) => avatar with
        { ClearAvatarId = 0, ClearAvatarSourceUid = 0, Color1 = 0, Color2 = 0 };

    public static bool ShouldClearCloneTitle(uint cloneTitleItemId, InventorySnapshot inventory, ItemCatalog catalog) =>
        cloneTitleItemId != 0 &&
        (cloneTitleItemId > int.MaxValue ||
         catalog.TryGet((int)cloneTitleItemId, out var source) && source.CannotBeCloneTitle ||
         inventory.Find(new(InventoryListType.Equipment, 13)) is { } worn &&
         catalog.TryGet(worn.Core.ItemId, out var wornTemplate) && wornTemplate.CannotBeHiddenByCloneTitle);

    public static uint? CloneTitleAfter(Character character, InventorySnapshot after, ItemCatalog catalog) =>
        ShouldClearCloneTitle(character.CloneTitleItemId, after, catalog) ? 0u : null;

    public static InventoryResult PlanCleanup(Character character, ItemCatalog catalog)
    {
        var before = character.Inventory;
        var index = new InventoryIndex(before);
        var changed = false;
        var items = before.Items.Select(item =>
        {
            if (item.Avatar is not { } avatar || avatar.ClearAvatarId == 0 && avatar.ClearAvatarSourceUid == 0 ||
                catalog.Get(item.Core.ItemId) is not { CloneAvatar: true, BorrowAvatarAppearance: true })
                return item;
            var source = avatar.ClearAvatarSourceUid > 0 ? index.FindUid(avatar.ClearAvatarSourceUid) : null;
            if (avatar.ClearAvatarId > 0 && source is { Avatar: not null, Position.List: InventoryListType.Avatar } &&
                source.Uid != item.Uid && source.Core.ItemId == avatar.ClearAvatarId)
                return item;
            changed = true;
            return item with { Avatar = Clear(avatar) };
        }).ToImmutableArray();
        var after = changed ? before with { Items = items } : before;
        // 同步当前橱窗，避免客户端仍显示清理前的穿戴快照。
        return AvatarPresetService.SyncActive(new(true, 0,
            new(before, after, "clone_avatar_source_cleanup", new(character))
            {
                CloneTitleItemIdAfter = CloneTitleAfter(character, after, catalog)
            }));
    }
}
