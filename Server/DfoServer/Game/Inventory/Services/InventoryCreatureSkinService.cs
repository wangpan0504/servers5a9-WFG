using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryCreatureSkinService(ItemCatalog items, TimeProvider? clock = null)
{
    internal const byte UnlockFlag = 32;
    internal const ushort TargetSlot = 33;
    internal const byte AuraUnlockFlag = 8;
    internal const ushort AuraTargetSlot = 11;
    internal const int AuraTicketId = 490700411;
    internal const uint AuraAction = 127;
    // 这是已证商城定位分支的兼容选择，不宣称恢复了原服错误码的业务名称。
    internal const ushort PurchaseGuide = 1;
    internal const ushort Rejection = 0;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public static int AppearanceTemplateId(Character character, long utcSeconds)
    {
        if ((character.EquipmentSlotFlags & UnlockFlag) == 0)
            return 0;
        var real = character.Inventory.Find(new(InventoryListType.Equipment, 26));
        var skin = character.Inventory.Find(new(InventoryListType.Equipment, (short)TargetSlot));
        return real?.Creature is not null && !real.IsExpired(utcSeconds) && skin?.Creature is not null && !skin.IsExpired(utcSeconds)
            ? skin.Core.ItemId : 0;
    }

    public InventoryResult PlanUnlock(Character character, short sourceSlot, ushort target, long utcSeconds)
    {
        var source = character.Inventory.Find(new(InventoryListType.Main, sourceSlot));
        return PlanUnlock(character, sourceSlot, target, utcSeconds, source is null ? null : items.Get(source.Core.ItemId));
    }

    public InventoryResult PlanAuraAction(Character character, ushort sourceSlot, byte list, uint action, long utcSeconds)
    {
        if (sourceSlot > short.MaxValue || list != (byte)InventoryListType.Main || action != AuraAction)
            return InventoryResult.Reject(Rejection);
        var source = character.Inventory.Find(new(InventoryListType.Main, (short)sourceSlot));
        if (source?.Core.ItemId != AuraTicketId)
            return InventoryResult.Reject(Rejection);
        var template = items.Get(source.Core.ItemId);
        return PlanAuraAction(character, (short)sourceSlot, utcSeconds, template);
    }

    internal static InventoryResult PlanAuraAction(Character character, short sourceSlot, long utcSeconds, ItemTemplate? template) =>
        template?.OpensAuraSkinSlot == true
            ? PlanUnlock(character, sourceSlot, AuraTargetSlot, utcSeconds, template)
            : InventoryResult.Reject(Rejection);

    internal static InventoryResult PlanUnlock(Character character, short sourceSlot, ushort target, long utcSeconds,
        ItemTemplate? template)
    {
        byte flag = target switch { TargetSlot => UnlockFlag, AuraTargetSlot => AuraUnlockFlag, _ => 0 };
        if (flag == 0 || (character.EquipmentSlotFlags & flag) != 0)
            return InventoryResult.Reject(Rejection);
        if (sourceSlot == -1)
            return InventoryResult.Reject(PurchaseGuide);
        var before = character.Inventory;
        var position = new InventorySlot(InventoryListType.Main, sourceSlot);
        var source = before.Find(position);
        ItemKind kind = target == AuraTargetSlot ? ItemKind.Material : ItemKind.Consumable;
        bool validTicket = target == AuraTargetSlot
            ? template is { Id: AuraTicketId, Kind: ItemKind.Material, StackableType: "[material] 0" } &&
              source?.Core.ItemId == AuraTicketId
            : template?.OpensCreatureSkinSlot == true;
        if (source is null || source.Uid <= 0 || source.Core.Kind != kind || source.Core.Count <= 0 ||
            source.Core.EquipmentLockId != 0 || source.IsExpired(utcSeconds) ||
            !ItemSlotBoundService.Allows(before, position, kind) || template is null ||
            template.Id != source.Core.ItemId || template.Kind != kind || !validTicket ||
            !template.AllowsUse(character.Level, character.Job) || !InventoryMoveService.RequiredSkillSatisfied(character, template))
            return InventoryResult.Reject(Rejection);
        var deletion = InventoryDeleteService.Plan(before, position, source.Core.ItemId, 1);
        if (!deletion.Accepted || deletion.Plan is null)
            return InventoryResult.Reject(Rejection);
        return new(true, 0, new(before, deletion.Plan.After, target == AuraTargetSlot ? "open_aura_skin_slot" : "open_creature_skin_slot",
            new(character, source.ExpireTime == 0 ? null : source.ExpireTime))
            { EquipmentSlotFlagsAfter = (byte)(character.EquipmentSlotFlags | flag) });
    }
}
