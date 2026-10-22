using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ClassChangeTicketDefinition(int TemplateId, int MinimumLevel, int MaximumLevel, ushort Jobs,
    byte? FixedTarget, ImmutableHashSet<(byte Job, byte Grow)> Forbidden,
    ImmutableHashSet<byte> SourceGrowTypes, bool AllowUntransferred)
{
    public static ClassChangeTicketDefinition? Parse(StackableFile raw, ItemTemplate template)
    {
        var action = StackableActionCatalog.Arguments(raw);
        if (raw.TemplateId != template.Id || template.Kind != ItemKind.Consumable || template.UseForbidden ||
            raw.TypeDeclaration is not { Label: "[waste]", Parameters: [0] } ||
            action is not { Label: "[class change]" } ||
            action.Parameters.Count > 1 || action.Parameters.Any(value => value is < 1 or > 5) ||
            raw.DeclaredMinimumLevel is not { } minimum || minimum < 1 ||
            raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null || raw.DeclaredMaximumLevel < minimum ||
            raw.DeclaredUsableJobs is not { Count: > 0 } ||
            raw.DeclaredActionUsablePlaces is not { } places || !places.Contains("[village]") ||
            places.Any(place => place is not ("[village]" or "[seria room]" or "[dont use assult]")) ||
            raw.DeclaredActionUsableServerTypes is not { } servers || !servers.Contains("[general server]") ||
            servers.Any(server => server is not ("[general server]" or "[event server]")) ||
            raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 ||
            raw.DeclaredDontChangeGrowTypes is not { } forbidden || forbidden.Count % 2 != 0 ||
            raw.DeclaredUsableFirstGrowTypes is not { } sources || sources.Any(value => value is < 0 or > 5) ||
            raw.UnsupportedFields.Contains("class change grow type check off")) return null;
        var bans = ImmutableHashSet.CreateBuilder<(byte, byte)>();
        for (int i = 0; i < forbidden.Count; i += 2)
        {
            if (forbidden[i] is < 0 or > 15 || forbidden[i + 1] is < 1 or > 5) return null;
            bans.Add(((byte)forbidden[i], (byte)forbidden[i + 1]));
        }
        return new(template.Id, minimum, raw.DeclaredMaximumLevel ?? int.MaxValue, template.Jobs,
            action.Parameters.Count == 0 ? null : (byte)action.Parameters[0], bans.ToImmutable(),
            sources.Select(value => (byte)value).ToImmutableHashSet(), raw.ClassChangeGrowTypeCheckOff);
    }
}
