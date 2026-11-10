using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ActionConsumableDefinition(ItemTemplate Item, string Label, ImmutableArray<int> Parameters,
    int CoolTime, int CoolGroup, byte Maintenance, byte DailyLimit, ImmutableArray<string> Places)
{
    public const uint RidingAction = 100;
    public const uint StoryBookAction = 139;
    public int EffectMilliseconds { get; init; }
    public byte EffectMaintenance { get; init; }
    public ImmutableHashSet<int> AllowedTargets { get; init; } = [];
    public ImmutableHashSet<uint> ExcludedDungeons { get; init; } = [];
    public ImmutableHashSet<uint>? AllowedDungeons { get; init; }
    public bool RaidUseForbidden { get; init; }
    // [village] 覆盖整座城镇（含赛丽亚房间），与强化/宠物地点判定同一口径；[seria room] 仍只限房间。
    public bool AllowsPlace(string place) => StackableUsePlaces.Allows(Places, place);
    // [action usable dungeon] 与普通 STK 一致交客户端 029B9DB0 核对，服务端保留禁用副本与团本限制。
    public bool AllowsDungeon(uint dungeonId, bool inRaid = false) =>
        dungeonId > 0 && (!RaidUseForbidden || !inRaid) && !ExcludedDungeons.Contains(dungeonId);

    public static ActionConsumableDefinition? Parse(RecoveryConsumableFile file, ItemTemplate item)
    {
        var raw = file.Stackable;
        var action = StackableActionCatalog.Arguments(raw);
        if (raw.TemplateId != item.Id || item.Kind != ItemKind.Consumable || item.UseForbidden ||
            file.StructureDiagnostics.Length != 0 || raw.TypeDeclaration is null ||
            action is not ({ Label: "[rent point]" or "[add mileage]" or "[use favor point]", Parameters: [> 0] } or
                { Label: "[repair coupon]" or "[repair equipment]", Parameters.Count: 0 } or
                { Label: "[charm charging]", Parameters: [> 0, > 0, ..] } or
                { Label: "[add dimension dungeon enter count]", Parameters: [> 0 and <= 5] } or
                { Label: "[story book]", Parameters: [> 0 and < 30000] } or
                // 装扮预设扩展券（CMD515 action216）：无参数，容量由所属玩法落库。
                { Label: "[add avatar preset]", Parameters.Count: 0 } or
                { Label: "[character name change]", Parameters.Count: 0 } or
                { Label: "[add dungeon incount item limit count]", Parameters: [> 0, 0, 0] } or
                { Label: "[add dungeon incount item limit count]", Parameters: [8523, -1, -1] } or
                { Label: "[add dungeon incount item limit count]", Parameters: [291100320, 10305620, 10306736] } or
                { Label: "[add enter limit dungeonincount]" or "[add account enter limit dungeonincount]", Parameters.Count: >= 2 } or
                { Label: "[init dimension dungeon count]", Parameters.Count: >= 2 } or
                { Label: "[dungeon reward reset]", Parameters: [28, 0] } or
                { Label: "[reset single dungeon]", Parameters: [>= 1 and <= 4] } or
                { Label: "[riding]", Parameters: [> 0, > 0] } or
                { Label: "[add crackofdaybreak dungeon enter count]", Parameters: [> 0 and <= 10000] } or
                { Label: "[reset crackofdaybreak dungeon enter count]", Parameters.Count: 0 } or
                { Label: "[extend item period]", Parameters: [0, > 0] } or
                { Label: "[creature transport stone]" or "[cargo transport stone]" or "[avatar transport stone]" or
                    "[flag transport stone]" or "[emblem transport stone]", Parameters: [>= 0] } or
                { Label: "[change creature random]", Parameters.Count: >= 5 } or
                { Label: "[change creature]", Parameters.Count: 4 } or
                { Label: "[creature effect]", Parameters: [> 0] }) ||
            (action.Label is "[rent point]" or "[add mileage]" ? raw.DeclaredUseActionPacket != 1 :
                action.Label is "[add dungeon incount item limit count]" or "[add enter limit dungeonincount]" or
                    "[add account enter limit dungeonincount]" or "[init dimension dungeon count]" or
                    "[dungeon reward reset]" or "[reset single dungeon]" or "[riding]" ? raw.DeclaredUseActionPacket is not (null or 1) :
                raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0) ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            file.DeclaredFields.Contains("action usable dungeon") && file.ActionUsableDungeons is null ||
            file.LimitDungeons.IsDefault || file.LimitDungeons.Any(id => id <= 0) || file.ImpossibleContents.IsDefault ||
            file.DeclaredFields.Contains("cool time") && file.CoolTime is null or < 0 ||
            file.DeclaredFields.Contains("cooltime group") && file.CoolGroup is null or < 0 ||
            file.DeclaredFields.Contains("cooltime maintenance") && file.CooltimeMaintenance is null ||
            file.DeclaredFields.Contains("total usable count") && file.TotalUsableCount is null or < 0 or > byte.MaxValue ||
            file.DeclaredFields.Contains("effect maintenance") && (action.Label != "[creature effect]" || file.EffectMaintenance is null) ||
            file.DeclaredFields.Any(field => field is "booster info" or "increase status type" or "hp recovery" or "mp recovery" or "stat change" or "stat change duration" or
                "total weekly usable count" or "total accumulated usable count" or "use count limit all server item"))
            return null;
        bool extension = action.Label == "[extend item period]";
        if (extension && (raw.DeclaredActionLimitIndexes is not { Count: > 0 } limits || limits.Any(id => id <= 0) ||
            raw.DeclaredActionResultLimitIndexes is not { Count: 0 })) return null;
        return new(item, action.Label, action.Parameters.ToImmutableArray(), file.CoolTime ?? 0, file.CoolGroup ?? -item.Id,
            file.CooltimeMaintenance ?? 0, checked((byte)(file.TotalUsableCount ?? 0)), raw.DeclaredActionUsablePlaces?.ToImmutableArray() ?? [])
        {
            EffectMilliseconds = action.Label == "[creature effect]" ? action.Parameters[0] : 0,
            EffectMaintenance = file.EffectMaintenance ?? 0,
            AllowedTargets = extension ? raw.DeclaredActionLimitIndexes!.ToImmutableHashSet() : [],
            AllowedDungeons = file.ActionUsableDungeons?.Select(id => checked((uint)id)).ToImmutableHashSet(),
            ExcludedDungeons = file.LimitDungeons.Select(id => checked((uint)id)).ToImmutableHashSet(),
            RaidUseForbidden = file.ImpossibleContents.Contains("raid dungeon", StringComparer.OrdinalIgnoreCase)
        };
    }
}
