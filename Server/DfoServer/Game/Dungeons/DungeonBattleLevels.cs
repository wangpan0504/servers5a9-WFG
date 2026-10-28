using System.Collections.Frozen;
using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonBattleLevelRule(uint DungeonId, int? MapId, int Offset, int? MaximumLevel)
{
    public int Apply(int original) => Offset == 0 ? original :
        (int)Math.Clamp((long)original + Offset, 1, MaximumLevel!.Value);
}

// 发布完整不可变快照；读取房间时不做文件IO，也不持有配置写锁。
internal sealed class DungeonBattleLevelSnapshot
{
    readonly FrozenDictionary<(uint Dungeon, int? Map), DungeonBattleLevelRule> rules;
    public IReadOnlyList<DungeonBattleLevelRule> Rules { get; }

    public DungeonBattleLevelSnapshot(IEnumerable<DungeonBattleLevelRule> source)
    {
        var rows = source.ToArray();
        if (rows.Length > 1024 || rows.Any(rule => rule is null || rule.DungeonId == 0 || rule.MapId is <= 0 ||
            rule.MaximumLevel is < 1 or > byte.MaxValue || rule.Offset != 0 && rule.MaximumLevel is null) ||
            rows.Select(rule => (rule.DungeonId, rule.MapId)).Distinct().Count() != rows.Length)
            throw new InvalidDataException("怪物等级规则必须有唯一副本/地图身份，非零偏移必须明确提供1至255的上限");
        Rules = Array.AsReadOnly(rows);
        rules = rows.ToFrozenDictionary(rule => (rule.DungeonId, rule.MapId));
    }

    public DungeonBattleLevelRule? Resolve(uint dungeon, int map) =>
        rules.GetValueOrDefault((dungeon, map)) ?? rules.GetValueOrDefault((dungeon, null));
}

internal sealed class DungeonBattleLevelSettings
{
    DungeonBattleLevelSnapshot current = new([]);
    public DungeonBattleLevelSnapshot Current => Volatile.Read(ref current);
    public void Replace(IEnumerable<DungeonBattleLevelRule> rules)
    {
        var next = new DungeonBattleLevelSnapshot(rules);
        Volatile.Write(ref current, next);
    }
}

// 同一个组队实例的角色投影共享房间规则，热改不能令同房队员冻结不同等级。
internal sealed class DungeonBattleLevelRooms(DungeonBattleLevelSettings? settings)
{
    readonly object gate = new();
    readonly Dictionary<DungeonMap, DungeonBattleLevelRule?> frozen = [];
    public DungeonBattleLevelRule? Freeze(uint dungeon, DungeonMap map)
    {
        lock (gate)
        {
            if (!frozen.TryGetValue(map, out var rule))
                frozen.Add(map, rule = settings?.Current.Resolve(dungeon, map.Id));
            return rule;
        }
    }
}

internal static class DungeonBattleLevels
{
    public static DungeonMonsterPlan Freeze(DungeonMonsterPlan plan, DungeonBattleLevelRule? rule)
    {
        if (rule is null || rule.Offset == 0) return plan;
        var actors = plan.Altar?.Actors.ToDictionary(actor => actor.ObjectKey,
            actor => actor with { BattleLevelOverride = checked((byte)rule.Apply(actor.Level)) });
        var monsters = plan.Monsters.Select(monster => monster.SpawnType <= 3 && monster.AiCharacter is null && !monster.IsFriendly
            ? monster with { BattleLevelOverride = rule.Apply(monster.CandidateLevel),
                AltarActor = actors is null ? monster.AltarActor : actors[monster.ObjectKey] }
            : monster).ToArray();
        DungeonAltarPlan? altar = plan.Altar;
        if (altar is not null)
            altar = altar with
            {
                Actors = altar.Actors.Select(actor => actors![actor.ObjectKey]).ToImmutableArray(),
                Rounds = altar.Rounds.Select(round => round with
                {
                    Batches = round.Batches.Select(batch => batch with
                    { Actors = batch.Actors.Select(actor => actors![actor.ObjectKey]).ToImmutableArray() }).ToImmutableArray()
                }).ToImmutableArray()
            };
        return plan with
        {
            Monsters = Array.AsReadOnly(monsters), Altar = altar,
            HellBattleLevelOverrides = monsters.Where(monster => monster.HellWave is not null && monster.BattleLevelOverride is not null)
                .ToFrozenDictionary(monster => monster.ObjectKey, monster => checked((byte)monster.BattleLevel))
        };
    }
}
