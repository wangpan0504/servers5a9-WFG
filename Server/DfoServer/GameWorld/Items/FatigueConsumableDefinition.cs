using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record FatigueConsumableDefinition(ItemTemplate Item, int Amount, int CoolTime, int CoolGroup,
    byte CooltimeMaintenance, ImmutableArray<string> Places, byte DailyLimit)
{
    public const uint Action = 58;
    public bool AllowsPlace(string place, bool inAssault) =>
        !(inAssault && Places.Contains("[dont use assult]")) && (Places.Contains("[all]") || Places.Contains(place));

    public static FatigueConsumableDefinition? Parse(RecoveryConsumableFile file, ItemTemplate item)
    {
        var raw = file.Stackable;
        var action = StackableActionCatalog.Arguments(raw);
        if (raw.TemplateId != item.Id || item.Kind != ItemKind.Consumable || item.UseForbidden || file.StructureDiagnostics.Length != 0 ||
            action is not { Label: "[add fatigue]", Parameters: [> 0] } ||
            raw.TypeDeclaration is null ||
            !raw.HasUseActionPacket || raw.DeclaredUseActionPacket != 1 ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            raw.HasActionUsablePlace && raw.DeclaredActionUsablePlaces is not { Count: > 0 } ||
            raw.DeclaredActionUsablePlaces?.Any(place => place is not ("[village]" or "[seria room]" or "[dungeon]" or "[all]" or "[dont use assult]")) == true ||
            file.DeclaredFields.Contains("action usable server type") ||
            file.DeclaredFields.Contains("cool time") && file.CoolTime is null or < 0 ||
            file.DeclaredFields.Contains("cooltime group") && file.CoolGroup is null or < 0 ||
            file.DeclaredFields.Contains("cooltime maintenance") && file.CooltimeMaintenance is null ||
            file.DeclaredFields.Contains("total usable count") && file.TotalUsableCount is null or < 0 or > byte.MaxValue ||
            file.DeclaredFields.Any(field => field is "booster info" or "increase status type" or "stat change" or
                "hp recovery" or "mp recovery" or "stat change duration" or "effect maintenance" or "sub type" or
                "total weekly usable count" or "total accumulated usable count" or "use count limit all server item" or
                "usable first growtype" or "dont change grow type" or "impossible jobs"))
            return null;
        return new(item, action.Parameters[0], file.CoolTime ?? 0, file.CoolGroup ?? -item.Id,
            file.CooltimeMaintenance ?? 0, raw.HasActionUsablePlace ? raw.DeclaredActionUsablePlaces!.ToImmutableArray() : ["[all]"],
            checked((byte)(file.TotalUsableCount ?? 0)));
    }
}
