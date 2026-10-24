using PvfLib;

namespace DfoServer.GameWorld.Items;

// 本版扩展券只提高一个背包档位；商店兑换材料不属于使用成本。
internal sealed record InventoryExpansionDefinition(ItemTemplate Item)
{
    public static InventoryExpansionDefinition? Parse(RecoveryConsumableFile file, ItemTemplate item)
    {
        var raw = file.Stackable;
        if (raw.TemplateId != item.Id || file.TemplateId != item.Id || item.Kind != ItemKind.Consumable || item.UseForbidden ||
            file.StructureDiagnostics.Length != 0 || raw.TypeDeclaration is not { Label: "[etc]", Parameters: [0] } ||
            StackableActionCatalog.Arguments(raw) is not { Label: "[upgrade inventory]", Parameters.Count: 0 } ||
            raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            raw.DeclaredActionUsablePlaces is not { Count: > 0 } places || !places.Contains("[village]") ||
            places.Any(place => place is not ("[village]" or "[seria room]")) ||
            file.DeclaredFields.Any(field => field is not ("name" or "explain" or "flavor text" or "grade" or "rarity" or
                "usable job" or "attach type" or "minimum level" or "maximum level" or "icon" or "icon mark" or
                "stackable type" or "move wav" or "expiration date" or "action type" or "stack limit" or
                "action usable place" or "usable period" or "impossible contents" or "item category" or
                "need material" or "purchase limit" or "use action packet")))
            return null;
        return new(item);
    }
}
