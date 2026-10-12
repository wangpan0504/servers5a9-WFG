using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed record InventoryDiscardRequest(ushort X, ushort Y, byte List, ushort Slot, uint Count, byte Tail);
internal sealed record InventoryDiscardPlan(InventoryDiscardRequest Request, InventoryItem Source, ItemCore GroundCore, InventoryPlan Inventory)
{
    public static (InventoryDiscardPlan? Plan, ushort Error) Prepare(Character character, InventoryDiscardRequest request,
        ItemTemplate template, long now)
    {
        if (request.List is not (0 or 3) || request.Slot > short.MaxValue)
            return (null, 19);
        var source = character.Inventory.Find(new((InventoryListType)request.List, (short)request.Slot));
        if (source is null || request.Count == 0 || request.Count > source.Core.Count ||
            !source.Core.IsStackable && request.Count != 1)
            return (null, 17);
        if (source.IsExpired(now)) return (null, 104);
        if (source.Avatar is not null || source.Creature is not null || source.Core.Kind is ItemKind.Avatar or ItemKind.Creature || source.Core.IsRental ||
            source.Core.EquipmentLockId != 0 || source.Core.TradeRestriction != 0 ||
            template.Id != source.Core.ItemId || template.Kind != source.Core.Kind)
            return (null, 19);
        string attach = template.AttachType.Replace("`", "").Trim().Trim('[', ']').Trim().ToLowerInvariant();
        bool tradable = attach is "free" or "trade" || attach is "sealing" or "seal" && source.Core.SealFlag != 0 ||
            attach == "trade limit" && source.Core.Attr >> 5 != 0;
        if (!tradable) return (null, 19);
        var deleted = InventoryDeleteService.Plan(character.Inventory, source.Position, source.Core.Value, checked((int)request.Count));
        if (!deleted.Accepted) return (null, 19);
        var core = source.Core.IsStackable ? source.Core with { Value = checked((int)request.Count) } : source.Core;
        return (new(request, source, core, new InventoryPlan(character.Inventory, deleted.Plan!.After, "dungeon_inventory_discard",
            new InventoryCharacterPrecondition(character))), 0);
    }
}
