using System.Collections.Frozen;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 四项AI参数的线上模式；Native表示恢复客户端原生计算，固定值与倍率互斥。
internal enum MonsterAiMode : uint { Native = 0, Fixed = 1, Multiplier = 2 }

internal readonly record struct MonsterAiValue(MonsterAiMode Mode, float Number)
{
    public static MonsterAiValue Native => new(MonsterAiMode.Native, 0);
}

// 配置里的一项：只填Value为固定值，只填Multiplier为倍率，都不填是明确恢复原生，null表示继承副本默认。
internal sealed record MonsterAiParameter
{
    public float? Value { get; init; }
    public float? Multiplier { get; init; }

    // 固定值转成float32后仍须落在非负int范围内；2147483647会先变成2147483648，因此必须按double比较。
    public bool IsValid => Value is { } fixedValue
        ? Multiplier is null && float.IsFinite(fixedValue) && fixedValue >= 0 && (double)fixedValue <= int.MaxValue
        : Multiplier is not { } multiplier || float.IsFinite(multiplier) && multiplier >= 0;

    public MonsterAiValue Resolve() => Value is { } value ? new(MonsterAiMode.Fixed, value) :
        Multiplier is { } multiplier ? new(MonsterAiMode.Multiplier, multiplier) : MonsterAiValue.Native;
}

// 未填写的字段为null：地图规则继承副本默认，副本默认规则或完全没有规则时保持原生。
internal sealed record MonsterAiRule(uint DungeonId, int? MapId, MonsterAiParameter? Warlike = null, MonsterAiParameter? Sight = null,
    MonsterAiParameter? MoveSpeed = null, MonsterAiParameter? AttackSpeed = null,
    MonsterAiParameter? HitPoints = null, MonsterAiParameter? Defense = null);

internal sealed record MonsterAiProfile(MonsterAiValue Warlike, MonsterAiValue Sight, MonsterAiValue MoveSpeed, MonsterAiValue AttackSpeed,
    MonsterAiValue HitPoints = default, MonsterAiValue Defense = default)
{
    public static MonsterAiProfile Native { get; } = new(MonsterAiValue.Native, MonsterAiValue.Native, MonsterAiValue.Native, MonsterAiValue.Native);
}

// 发布完整不可变快照；读取房间时不做文件IO，也不持有配置写锁。
internal sealed class DungeonMonsterAiSnapshot
{
    readonly FrozenDictionary<(uint Dungeon, int? Map), MonsterAiRule> rules;
    public bool Enabled { get; }
    public IReadOnlyList<MonsterAiRule> Rules { get; }

    public DungeonMonsterAiSnapshot(IEnumerable<MonsterAiRule> source, bool enabled)
    {
        var rows = source.ToArray();
        if (rows.Length > 1024 || rows.Any(rule => rule is null || rule.DungeonId == 0 && rule.MapId is not null || rule.MapId is <= 0 ||
            !AllParametersValid(rule)) ||
            rows.Select(rule => (rule.DungeonId, rule.MapId)).Distinct().Count() != rows.Length)
            throw new InvalidDataException("怪物AI规则必须有唯一副本/地图身份，四项参数只能设置固定值或倍率之一，且必须为非负有限float32");
        Enabled = enabled;
        Rules = Array.AsReadOnly(rows);
        rules = rows.ToFrozenDictionary(rule => (rule.DungeonId, rule.MapId));
    }

    static bool AllParametersValid(MonsterAiRule rule) => new[] { rule.Warlike, rule.Sight, rule.MoveSpeed, rule.AttackSpeed,
        rule.HitPoints, rule.Defense }.All(parameter => parameter is null || parameter.IsValid) &&
        rule.HitPoints?.Value is null && rule.Defense?.Value is null;

    // 地图规则逐字段覆盖副本默认；没有任何匹配时四项全部原生。
    public MonsterAiProfile Resolve(uint dungeon, int map)
    {
        rules.TryGetValue((dungeon, null), out var fallback);
        rules.TryGetValue((dungeon, map), out var exact);
        rules.TryGetValue((0, null), out var global);
        return new(Field(exact?.Warlike, fallback?.Warlike, global?.Warlike), Field(exact?.Sight, fallback?.Sight, global?.Sight),
            Field(exact?.MoveSpeed, fallback?.MoveSpeed, global?.MoveSpeed), Field(exact?.AttackSpeed, fallback?.AttackSpeed, global?.AttackSpeed),
            Field(exact?.HitPoints, fallback?.HitPoints, global?.HitPoints), Field(exact?.Defense, fallback?.Defense, global?.Defense));
        // 非空对象本身也是明确配置，不能用“里面没有数字”判断为缺失。
        static MonsterAiValue Field(MonsterAiParameter? first, MonsterAiParameter? second, MonsterAiParameter? third) =>
            (first ?? second ?? third)?.Resolve() ?? MonsterAiValue.Native;
    }
}

internal sealed class DungeonMonsterAiSettings
{
    DungeonMonsterAiSnapshot current = new([], false);
    public DungeonMonsterAiSnapshot Current => Volatile.Read(ref current);
    public void Replace(IEnumerable<MonsterAiRule> rules, bool enabled)
    {
        var next = new DungeonMonsterAiSnapshot(rules, enabled);
        Volatile.Write(ref current, next);
    }
}

// 同一个组队实例的角色投影共享本次房间访问的快照，热改不能令同房队员拿到不同参数；
// 回访或换房按当时配置重新解析，因此只保留最近一次访问。
internal sealed class DungeonMonsterAiRooms(DungeonMonsterAiSettings? settings)
{
    readonly object gate = new();
    (uint Dungeon, int Map)? frozen;
    MonsterAiProfile? profile;

    public MonsterAiProfile? Freeze(uint dungeon, DungeonMap map)
    {
        lock (gate)
        {
            if (frozen is { } current && current.Dungeon == dungeon && current.Map == map.Id) return profile;
            frozen = (dungeon, map.Id);
            var snapshot = settings?.Current;
            profile = snapshot is { Enabled: true } ? snapshot.Resolve(dungeon, map.Id) : null;
            return profile;
        }
    }
}
