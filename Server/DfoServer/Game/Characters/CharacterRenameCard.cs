using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Characters;

internal interface ICharacterRenameCardStore
{
    Character? ActivateRenameCard(AccountOwners.Lease owner, Character expected, InventoryPlan inventory);
    CharacterRenameFailure CompleteRenameCard(AccountOwners.Lease owner, Character expected, string newName);
}

internal enum CharacterRenameFailure { None, Unavailable, InvalidName, Duplicate }

internal static class CharacterRenameCard
{
    public static bool AllowsMarriage(MarriageRecord? marriage) => marriage?.State is not (1 or 2 or 3);

    public static InventoryResult Prepare(Character character, ushort slot, ItemCatalog items, string place, TimeProvider clock)
    {
        var position = new InventorySlot(InventoryListType.Main, checked((short)slot));
        var source = character.Inventory.Find(position);
        var definition = source is null ? null : items.ActionConsumable(source.Core.ItemId);
        if (!Eligible(character, definition))
            return InventoryResult.Reject(19);
        var result = InventoryActionConsumableService.Plan(character, position, definition!, place, clock);
        return result.Plan is { } plan ? result with { Plan = new(plan.Before, plan.After, "character_rename_card", plan.CharacterPrecondition)
            { UsedItems = plan.UsedItems } } : result;
    }

    internal static bool Eligible(Character character, ActionConsumableDefinition? definition) =>
        definition is { Label: "[character name change]", Parameters.Length: 0 } &&
        definition.Item.AllowsUse(character.Level, character.Job) && AllowsMarriage(character.Marriage);
}
