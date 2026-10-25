using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Skills;

internal sealed class SkillSwitchInventoryService(
    ItemCatalog items,
    SwitchingSupportSkillCatalog supportSkills,
    TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public SkillSwitchInventoryResult Plan(DfoServer.Game.Characters.Character character, SkillSwitchInventoryRequest request)
    {
        character.SkillSwitchInventory.Validate();
        return request.Kind switch
        {
            SkillSwitchInventoryRequestKind.SetSkill => SetSkill(character, request),
            SkillSwitchInventoryRequestKind.SetItem => SetItem(character, request),
            SkillSwitchInventoryRequestKind.RemoveItem => RemoveItem(character, request),
            _ => new(SkillSwitchInventoryFailure.UnsupportedState)
        };
    }

    SkillSwitchInventoryResult SetSkill(DfoServer.Game.Characters.Character character, SkillSwitchInventoryRequest request)
    {
        if (request.SkillId != 0)
        {
            if (!supportSkills.Supports(character.Job, character.GrowType, request.SkillId))
                return new(SkillSwitchInventoryFailure.SkillNotSupported);
        }
        var after = character.SkillSwitchInventory.WithSkill(request.SkillId);
        return new(SkillSwitchInventoryFailure.None,
            new(character, after, request, "skill_switch_set_skill"));
    }

    SkillSwitchInventoryResult SetItem(DfoServer.Game.Characters.Character character, SkillSwitchInventoryRequest request)
    {
        if (!TryPosition(request.ListType, request.Slot, out InventorySlot position))
            return new(SkillSwitchInventoryFailure.ItemLocationInvalid);
        var item = character.Inventory.Find(position);
        if (item is null || item.Uid <= 0)
            return new(SkillSwitchInventoryFailure.ItemMissing);
        if (item.Core.Count != 1)
            return new(SkillSwitchInventoryFailure.ItemStacked);
        if (item.IsExpired(clock.GetUtcNow().ToUnixTimeSeconds()))
            return new(SkillSwitchInventoryFailure.ItemExpired);
        ItemTemplate template = items.Get(item.Core.ItemId);
        if (template.Id != item.Core.ItemId || template.Kind != item.Core.Kind ||
            template.EquipmentSlot != request.EquipmentPart || template.Kind is not
                (ItemKind.Equipment or ItemKind.Avatar or ItemKind.Creature or
                 ItemKind.CreatureEquipment or ItemKind.GuildMedal))
            return new(SkillSwitchInventoryFailure.ItemPartMismatch);
        if (!LocationMatches(request.EquipmentPart, position, item.Core.Kind, character.Inventory))
            return new(SkillSwitchInventoryFailure.ItemLocationInvalid);
        if (character.SkillSwitchInventory.Items.Any(binding =>
            binding.ItemUid == item.Uid && binding.EquipmentPart != request.EquipmentPart))
            return new(SkillSwitchInventoryFailure.DuplicateItem);
        var binding = new SkillSwitchItemBinding(request.EquipmentPart, item.Uid, item.Core.ItemId);
        var after = character.SkillSwitchInventory.WithBinding(binding);
        return new(SkillSwitchInventoryFailure.None,
            new(character, after, request, "skill_switch_set_item", item));
    }

    static SkillSwitchInventoryResult RemoveItem(
        DfoServer.Game.Characters.Character character,
        SkillSwitchInventoryRequest request)
    {
        var after = character.SkillSwitchInventory.WithoutBinding(request.EquipmentPart);
        return new(SkillSwitchInventoryFailure.None,
            new(character, after, request, "skill_switch_remove_item"));
    }

    internal static bool TryPosition(byte listType, ushort slot, out InventorySlot position)
    {
        position = default;
        if (slot > short.MaxValue)
            return false;
        InventoryListType list = listType switch
        {
            0 => InventoryListType.Main,
            1 => InventoryListType.Avatar,
            3 => InventoryListType.Equipment,
            7 => InventoryListType.Pet,
            38 => InventoryListType.GuildMedal,
            _ => (InventoryListType)byte.MaxValue
        };
        if (list == (InventoryListType)byte.MaxValue)
            return false;
        position = new(list, checked((short)slot));
        return true;
    }

    static bool LocationMatches(byte part, InventorySlot position, ItemKind kind, InventorySnapshot inventory)
    {
        if (!ItemSlotBoundService.Allows(inventory, position, kind))
            return false;
        return position.List switch
        {
            InventoryListType.Main => true,
            InventoryListType.Avatar => part <= 11,
            InventoryListType.Equipment => position.Index == part,
            InventoryListType.Pet => part is >= 26 and <= 29,
            InventoryListType.GuildMedal => part == 32,
            _ => false
        };
    }
}
