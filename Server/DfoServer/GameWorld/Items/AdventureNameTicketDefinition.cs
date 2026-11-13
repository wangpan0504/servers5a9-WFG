using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record AdventureNameTicketDefinition(int TemplateId, int MinimumLevel, int MaximumLevel, ushort Jobs)
{
    public static AdventureNameTicketDefinition? Parse(StackableFile raw, ItemTemplate template)
    {
        if (raw.TemplateId != template.Id || template.Kind != ItemKind.Consumable || template.UseForbidden ||
            raw.TypeDeclaration is not { Label: "[waste]", Parameters: [0] } ||
            StackableActionCatalog.Arguments(raw) is not { Label: "[represent account name change]", Parameters.Count: 0 } ||
            raw.DeclaredMinimumLevel is not { } minimum || minimum < 0 ||
            raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null || raw.DeclaredMaximumLevel < minimum ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 })
            return null;
        return new(template.Id, minimum, raw.DeclaredMaximumLevel ?? int.MaxValue, template.Jobs);
    }

    public bool Allows(byte level, byte job) => job < 16 && level >= MinimumLevel && level <= MaximumLevel;
}
