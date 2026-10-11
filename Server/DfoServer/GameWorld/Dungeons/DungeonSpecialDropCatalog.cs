using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal readonly record struct DungeonSpecialDropItem(int ItemId, int Weight);
internal sealed record DungeonIndependentDropRule(int EnemyType, int MonsterId, int ItemId,
    ImmutableArray<int> Probabilities, ImmutableArray<int> Attempts, int MinimumLevel, int MaximumLevel,
    int Difficulty, int ListFlag, ImmutableArray<int> ListValues)
{
    public ImmutableArray<DungeonSpecialDropItem> Items => ListFlag == 1 ?
        Enumerable.Range(0, ListValues.Length / 2).Select(index => new DungeonSpecialDropItem(ListValues[index * 2], ListValues[index * 2 + 1])).ToImmutableArray() : [];
}
internal sealed record DungeonWorldDropRule(int Level, int ReservedValue, ImmutableArray<DungeonSpecialDropItem> Items);
internal sealed record DungeonSpecialDropCompatibility(ImmutableArray<string> MissingPaths, int IndependentListReferences,
    int OtherEnemyTypes, int WorldReservedValues);

// 原整数及未知列表引用完整保留；业务只消费参考策略已声明的分支。
internal sealed class DungeonSpecialDropCatalog
{
    public const string IndependentPath = "etc/independent_drop.etc";
    public const string WorldPath = "etc/worlddrop.etc";
    public ImmutableDictionary<int, ImmutableArray<DungeonIndependentDropRule>> IndependentByMonster { get; }
    public ImmutableDictionary<int, DungeonWorldDropRule> WorldByLevel { get; }
    public ImmutableArray<string> MissingPaths { get; }
    public DungeonSpecialDropCompatibility Compatibility { get; }

    private DungeonSpecialDropCatalog(IEnumerable<DungeonIndependentDropRule> independent, IEnumerable<DungeonWorldDropRule> world,
        ImmutableArray<string> missing)
    {
        IndependentByMonster = independent.GroupBy(row => row.MonsterId).ToImmutableDictionary(group => group.Key, group => group.ToImmutableArray());
        WorldByLevel = world.ToImmutableDictionary(row => row.Level);
        MissingPaths = missing;
        var rows = IndependentByMonster.Values.SelectMany(values => values).ToArray();
        Compatibility = new(missing, rows.Count(row => row.ListFlag == 2), rows.Count(row => row.EnemyType != 0),
            WorldByLevel.Values.Count(row => row.ReservedValue != 0));
    }

    public static DungeonSpecialDropCatalog Load(PvfArchive pvf)
    {
        var missing = ImmutableArray.CreateBuilder<string>();
        var independent = pvf.Contains(IndependentPath) ? ParseIndependent(pvf.ReadTokens(IndependentPath), pvf.ResolveString) : MissingIndependent();
        var world = pvf.Contains(WorldPath) ? ParseWorld(pvf.ReadTokens(WorldPath), pvf.ResolveString) : MissingWorld();
        return new(independent, world, missing.ToImmutable());
        IEnumerable<DungeonIndependentDropRule> MissingIndependent() { missing.Add(IndependentPath); return []; }
        IEnumerable<DungeonWorldDropRule> MissingWorld() { missing.Add(WorldPath); return []; }
    }

    internal static DungeonSpecialDropCatalog Create(IEnumerable<DungeonIndependentDropRule> independent,
        IEnumerable<DungeonWorldDropRule> world) => new(independent, world, []);

    internal static ImmutableArray<DungeonIndependentDropRule> ParseIndependent(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        int at = Section(tokens, resolve, "[independent drop]");
        var rows = ImmutableArray.CreateBuilder<DungeonIndependentDropRule>();
        while (at < tokens.Count && !Tag(tokens[at], resolve, "[/independent drop]"))
        {
            var values = new int[17];
            for (int index = 0; index < values.Length; index++) values[index] = Integer(tokens, ref at, IndependentPath);
            int flag = values[16];
            if (values[0] < 0 || values[1] <= 0 || values[2] < 0 || values[13] < 0 || values[14] < 0 ||
                values[15] is < -1 or > 4 || flag is < 0 or > 2 ||
                values.AsSpan(3, 5).ContainsAnyExceptInRange(0, int.MaxValue) ||
                values.AsSpan(8, 5).ContainsAnyExceptInRange(0, ushort.MaxValue))
                throw new InvalidDataException("独立掉落声明的身份、概率、次数或条件非法: " + values[1]);
            var list = ImmutableArray.CreateBuilder<int>();
            if (flag != 0)
            {
                if (at >= tokens.Count || !Tag(tokens[at++], resolve, "[list]"))
                    throw new InvalidDataException("独立掉落列表缺少起始标签");
                while (at < tokens.Count && !Tag(tokens[at], resolve, "[/list]")) list.Add(Integer(tokens, ref at, IndependentPath));
                if (at == tokens.Count) throw new InvalidDataException("独立掉落列表未闭合");
                at++;
                if (flag == 1 && list.Count % 2 != 0) throw new InvalidDataException("独立掉落内联列表不是完整物品/权重对");
            }
            rows.Add(new(values[0], values[1], values[2], values.AsSpan(3, 5).ToArray().ToImmutableArray(),
                values.AsSpan(8, 5).ToArray().ToImmutableArray(), values[13], values[14], values[15], flag, list.ToImmutable()));
        }
        if (at == tokens.Count) throw new InvalidDataException("独立掉落块未闭合");
        return rows.ToImmutable();
    }

    internal static ImmutableArray<DungeonWorldDropRule> ParseWorld(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        int at = Section(tokens, resolve, "[world drop]");
        var rows = ImmutableArray.CreateBuilder<DungeonWorldDropRule>();
        var levels = new HashSet<int>();
        while (at < tokens.Count && !Tag(tokens[at], resolve, "[/world drop]"))
        {
            int level = Integer(tokens, ref at, WorldPath), reserved = Integer(tokens, ref at, WorldPath);
            if (level <= 0 || !levels.Add(level)) throw new InvalidDataException("世界掉落等级非法或重复");
            var items = ImmutableArray.CreateBuilder<DungeonSpecialDropItem>();
            int item;
            while ((item = Integer(tokens, ref at, WorldPath)) != -1)
                items.Add(new(item, Integer(tokens, ref at, WorldPath)));
            rows.Add(new(level, reserved, items.ToImmutable()));
        }
        if (at == tokens.Count) throw new InvalidDataException("世界掉落块未闭合");
        return rows.ToImmutable();
    }

    static int Section(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, string tag)
    {
        for (int at = 0; at < tokens.Count; at++) if (Tag(tokens[at], resolve, tag)) return at + 1;
        throw new InvalidDataException("掉落资源缺少标签: " + tag);
    }
    static bool Tag(PvfToken token, Func<int, string> resolve, string tag) => token.Type == 3 &&
        string.Equals(resolve(token.Value), tag, StringComparison.OrdinalIgnoreCase);
    static int Integer(IReadOnlyList<PvfToken> tokens, ref int at, string path)
    {
        if (at >= tokens.Count || tokens[at].Type != 0) throw new InvalidDataException("掉落资源整数行不完整: " + path + " @" + at);
        return tokens[at++].Value;
    }
}
