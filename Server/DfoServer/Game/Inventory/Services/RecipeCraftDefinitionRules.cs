using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed record RecipeCraftDefinitionError(ushort Code, string Reason);
internal static class RecipeCraftDefinitionRules
{
    // Shared by actual Craft and static consumers; no character state or random draw.
    public static RecipeCraftDefinitionError? ValidateOutputs(ItemRecipeDefinition definition,
        ItemCatalog items, EpicPieceCatalog pieces, ushort amount, out int successEquipmentCount)
    {
        successEquipmentCount = 0;
        var branches = new[] { definition.Outputs, definition.FailureOutputs };
        for (int branchIndex = 0; branchIndex < branches.Length; branchIndex++)
        {
            var branch = branches[branchIndex];
            int equipmentCount = 0;
            foreach (var output in branch)
            {
                long count = (long)output.Count * amount;
                var template = items.Get(output.ItemId);
                if (count is <= 0 or > int.MaxValue || template.Kind is not (ItemKind.Equipment or ItemKind.Consumable or ItemKind.Material) ||
                    template.IsPremiumService || template.AcquisitionContract is not null || pieces.ByPiece.ContainsKey(output.ItemId))
                    return new(InventoryRecipeCraftService.Rejection, "unsupported-output");
                if (template.Kind == ItemKind.Equipment && (count > 1 || ++equipmentCount > 1))
                    return new(23, "multiple-equipment-output");
            }
            if (branchIndex == 0) successEquipmentCount = equipmentCount;
        }
        return null;
    }
}
