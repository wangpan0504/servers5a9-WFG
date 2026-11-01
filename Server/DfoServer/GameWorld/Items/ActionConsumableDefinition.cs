using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ActionConsumableDefinition(ItemTemplate Item, string Label, ImmutableArray<int> Parameters,
    int CoolTime, int CoolGroup, byte Maintenance, byte DailyLimit, ImmutableArray<string> Places)
{
    public int EffectMilliseconds { get; init; }
    public byte EffectMaintenance { get; init; }
    public ImmutableHashSet<int> AllowedTargets { get; init; } = [];
    // [village] 覆盖整座城镇（含赛丽亚房间），与强化/宠物地点判定同一口径；[seria room] 仍只限房间。
    public bool AllowsPlace(string place) => Places.IsEmpty || Places.Contains("[all]") || Places.Contains(place) ||
        place == "[seria room]" && Places.Contains("[village]");

    public static ActionConsumableDefinition? Parse(RecoveryConsumableFile file, ItemTemplate item)
    {
        var raw = file.Stackable;
        var action = StackableActionCatalog.Arguments(raw);
        bool generalTransport = action?.Label is "[cargo transport stone]" or "[avatar transport stone]" or
            "[flag transport stone]" or "[emblem transport stone]" or "[creature transport stone]" &&
            raw.DeclaredActionUsableServerTypes is ["[general server]"];
        if (raw.TemplateId != item.Id || item.Kind != ItemKind.Consumable || item.UseForbidden ||
            file.StructureDiagnostics.Length != 0 || raw.TypeDeclaration is null ||
            action is not ({ Label: "[rent point]" or "[add mileage]" or "[use favor point]", Parameters: [> 0] } or
                { Label: "[repair coupon]" or "[repair equipment]", Parameters.Count: 0 } or
                { Label: "[add dimension dungeon enter count]", Parameters: [> 0 and <= 5] } or
                // 装扮预设扩展券（CMD515 action216）：无参数，容量由所属玩法落库。
                { Label: "[add avatar preset]", Parameters.Count: 0 } or
                { Label: "[character name change]", Parameters.Count: 0 } or
                { Label: "[add dungeon incount item limit count]", Parameters: [> 0, 0, 0] } or
                { Label: "[add dungeon incount item limit count]", Parameters: [8523, -1, -1] } or
                { Label: "[extend item period]", Parameters: [0, > 0] } or
                { Label: "[creature transport stone]" or "[cargo transport stone]" or "[avatar transport stone]" or
                    "[flag transport stone]" or "[emblem transport stone]", Parameters: [>= 0] } or
                { Label: "[change creature random]", Parameters.Count: >= 5 } or
                { Label: "[change creature]", Parameters.Count: 4 } or
                { Label: "[creature effect]", Parameters: [> 0] }) ||
            (action.Label is "[rent point]" or "[add mileage]" ? raw.DeclaredUseActionPacket != 1 :
                action.Label == "[add dungeon incount item limit count]" ? raw.DeclaredUseActionPacket is not (null or 1) :
                raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0) ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            raw.HasActionUsablePlace && raw.DeclaredActionUsablePlaces is not { Count: > 0 } ||
            raw.DeclaredActionUsablePlaces?.Any(place => place is not ("[village]" or "[seria room]" or "[dungeon]" or "[all]") &&
                !(action.Label == "[character name change]" && place == "[dont use assult]")) == true ||
            file.DeclaredFields.Contains("cool time") && file.CoolTime is null or < 0 ||
            file.DeclaredFields.Contains("cooltime group") && file.CoolGroup is null or < 0 ||
            file.DeclaredFields.Contains("cooltime maintenance") && file.CooltimeMaintenance is null ||
            file.DeclaredFields.Contains("total usable count") && file.TotalUsableCount is null or < 0 or > byte.MaxValue ||
            file.DeclaredFields.Contains("effect maintenance") && (action.Label != "[creature effect]" || file.EffectMaintenance is null) ||
            file.DeclaredFields.Any(field => field == "action usable server type" && !generalTransport || field is "usable game server type" or "usable server type" or
                "booster info" or "increase status type" or "hp recovery" or "mp recovery" or "stat change" or "stat change duration" or
                "total weekly usable count" or "total accumulated usable count" or "use count limit all server item"))
            return null;
        bool extension = action.Label == "[extend item period]";
        if (extension && (raw.DeclaredActionLimitIndexes is not { Count: > 0 } limits || limits.Any(id => id <= 0) ||
            raw.DeclaredActionResultLimitIndexes is not { Count: 0 })) return null;
        return new(item, action.Label, action.Parameters.ToImmutableArray(), file.CoolTime ?? 0, file.CoolGroup ?? -item.Id,
            file.CooltimeMaintenance ?? 0, checked((byte)(file.TotalUsableCount ?? 0)), raw.DeclaredActionUsablePlaces?.ToImmutableArray() ?? [])
        { EffectMilliseconds = action.Label == "[creature effect]" ? action.Parameters[0] : 0,
            EffectMaintenance = file.EffectMaintenance ?? 0,
            AllowedTargets = extension ? raw.DeclaredActionLimitIndexes!.ToImmutableHashSet() : [] };
    }
}
