using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.GameWorld.Creatures;

internal sealed record CreatureGrowthItemDefinition(int ItemId, int ViewMode, int GroupId, ImmutableArray<int> Types,
    int MinimumLevel, int MaximumLevel, int CreatureMinimumLevel, bool TownOnly)
{
    public const uint Action = 107;

    public static CreatureGrowthItemDefinition? Parse(StackableFile raw, ItemTemplate template)
    {
        var action = StackableActionCatalog.Arguments(raw);
        if (raw.TemplateId != template.Id || template.Kind != ItemKind.Consumable || template.UseForbidden ||
            raw.TypeDeclaration is not { Label: "[etc]" or "[waste]", Parameters: [0] } ||
            action is not { Label: "[creature growth]", Parameters.Count: >= 3 } ||
            action.Parameters[0] is < 0 or > 4 || action.Parameters[1] <= 0 || action.Parameters.Skip(2).Any(value => value is < 1 or > 4) ||
            raw.DeclaredMinimumLevel is not >= 1 || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.DeclaredMaximumLevel < raw.DeclaredMinimumLevel ||
            raw.HasCreatureMinimumLevel && raw.DeclaredCreatureMinimumLevel is not (>= 1 and <= 255) ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0)
            return null;
        return new(template.Id, action.Parameters[0], action.Parameters[1], action.Parameters.Skip(2).ToImmutableArray(),
            raw.DeclaredMinimumLevel.Value, raw.DeclaredMaximumLevel ?? int.MaxValue, raw.DeclaredCreatureMinimumLevel ?? 1,
            StackableUsePlaces.TownOnly(raw.DeclaredActionUsablePlaces));
    }
}
