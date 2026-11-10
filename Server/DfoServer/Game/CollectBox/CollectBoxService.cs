using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.CollectBox;

internal sealed class CollectBoxService(CollectBoxCatalog boxes, ItemCatalog items, EpicPieceCatalog epicPieces)
{
    public const ushort ErrorInventoryFull = 0x01;
    public const ushort ErrorMissing = 0x04;
    public const ushort ErrorInvalidItem = 0x12;
    public const ushort ErrorAlreadyStored = 0x17;
    public const ushort ErrorUnavailable = 0x5F;
    const long ExtensionSeconds = 24 * 60 * 60;

    public CollectBoxResult Select(Character character, byte box)
    {
        if (!boxes.TryGet(box, out _))
            return CollectBoxResult.Reject(ErrorUnavailable);
        return Accept(character, character.CollectBox.Select(box), NoInventory(character, "collectbox_select"));
    }

    public CollectBoxResult Add(Character character, CollectBoxAddRequest request, DateTimeOffset now)
    {
        if (!boxes.TryGet(request.Box, out var box))
            return CollectBoxResult.Reject(ErrorUnavailable);
        if (!box.TrySlot(request.ItemId, out var configured) || request.SourceSlot > short.MaxValue)
            return CollectBoxResult.Reject(ErrorInvalidItem);
        long seconds = now.ToUnixTimeSeconds();
        // 历史活动期限只投影给客户端显示；参考端不以它阻断放入或取出。
        if (character.CollectBox.Contains(request.Box, request.ItemId) ||
            character.CollectBox.Items.Any(item => item.Box == request.Box && item.PhysicalSlot == configured.PhysicalSlot))
            return CollectBoxResult.Reject(ErrorAlreadyStored);

        var source = character.Inventory.Find(new(InventoryListType.Main, checked((short)request.SourceSlot)));
        ItemTemplate template;
        try
        {
            template = items.Get(request.ItemId);
        }
        catch (InvalidDataException)
        {
            return CollectBoxResult.Reject(ErrorInvalidItem);
        }
        if (source is null || source.Core.ItemId != request.ItemId || source.Core.Kind != template.Kind ||
            !source.Core.IsStackable || source.Core.Count < 1 || source.Core.SortLockFlag != 0 ||
            source.Core.EquipmentLockId != 0 || source.IsExpired(seconds))
            return CollectBoxResult.Reject(ErrorInvalidItem);

        var removed = InventoryDeleteService.Plan(character.Inventory, source.Position, request.ItemId, 1);
        if (!removed.Accepted)
            return CollectBoxResult.Reject(ErrorInvalidItem);
        var inventory = Rename(removed.Plan!, "collectbox_add");
        var state = character.CollectBox.Add(request.Box, configured.PhysicalSlot, request.ItemId);
        return Accept(character, state, inventory);
    }

    public CollectBoxResult Remove(Character character, CollectBoxRemoveRequest request, DateTimeOffset now)
    {
        if (!boxes.TryGet(request.Box, out var box))
            return CollectBoxResult.Reject(ErrorMissing);
        if (!box.TrySlot(request.ItemId, out var configured))
            return CollectBoxResult.Reject(ErrorMissing);
        if (!character.CollectBox.Items.Contains(new(request.Box, configured.PhysicalSlot, request.ItemId)))
            return CollectBoxResult.Reject(ErrorMissing);

        ItemTemplate template;
        try
        {
            template = items.Get(request.ItemId);
        }
        catch (InvalidDataException)
        {
            return CollectBoxResult.Reject(ErrorMissing);
        }
        var core = InventoryRewardGrantService.Create(template, 1, now, shopExpiration: true);
        var granted = InventoryRewardGrantService.TryGrantCore(character, core, template, epicPieces, now);
        if (!granted.Accepted)
            return CollectBoxResult.Reject(ErrorInventoryFull);
        var inventory = Rename(granted.Plan!, "collectbox_remove");
        var state = character.CollectBox.Remove(request.Box, configured.PhysicalSlot, request.ItemId);
        return Accept(character, state, inventory);
    }

    public CollectBoxResult Extend(Character character, CollectBoxExtendRequest request, DateTimeOffset now)
    {
        if (!boxes.TryGet(request.Box, out var box))
            return CollectBoxResult.Reject(ErrorUnavailable);
        if (!box.TryExtension(request.ItemId, out var extension) || request.SourceSlot > short.MaxValue)
            return CollectBoxResult.Reject(ErrorInvalidItem);

        if (box.ResolveExpiration(character.CollectBox.Expiration(request.Box)) is null)
            return CollectBoxResult.Reject(ErrorMissing);

        long seconds = now.ToUnixTimeSeconds();
        long current = character.CollectBox.Expiration(request.Box) ?? box.BaseExpiration ?? seconds;
        if (box.MaximumExpiration is { } maximum && current >= maximum)
            return CollectBoxResult.Reject(ErrorMissing);
        long extended;
        try
        {
            extended = checked(current + ExtensionSeconds);
        }
        catch (OverflowException)
        {
            return CollectBoxResult.Reject(ErrorMissing);
        }
        if (box.MaximumExpiration is { } cap)
            extended = Math.Min(extended, cap);
        if (extended <= current)
            return CollectBoxResult.Reject(ErrorMissing);

        var source = character.Inventory.Find(new(InventoryListType.Main, checked((short)request.SourceSlot)));
        if (source is null || source.Core.ItemId != request.ItemId || !source.Core.IsStackable ||
            source.Core.Count < extension.RequiredCount || source.Core.SortLockFlag != 0 ||
            source.Core.EquipmentLockId != 0 || source.IsExpired(seconds))
            return CollectBoxResult.Reject(ErrorInvalidItem);
        var removed = InventoryDeleteService.Plan(character.Inventory, source.Position, request.ItemId, extension.RequiredCount);
        if (!removed.Accepted)
            return CollectBoxResult.Reject(ErrorInvalidItem);
        var inventory = Rename(removed.Plan!, "collectbox_extend");
        return Accept(character, character.CollectBox.Extend(request.Box, extended), inventory);
    }

    static CollectBoxResult Accept(Character character, CollectBoxState state, InventoryPlan inventory) =>
        CollectBoxResult.Accept(new(character, state, inventory));

    static InventoryPlan NoInventory(Character character, string reason) =>
        new(character.Inventory, character.Inventory, reason);

    static InventoryPlan Rename(InventoryPlan source, string reason) =>
        new(source.Before, source.After, reason, source.CharacterPrecondition, source.OverflowItems)
        {
            PreserveMaterials = source.PreserveMaterials,
            EquipmentSlotFlagsAfter = source.EquipmentSlotFlagsAfter
        };
}
