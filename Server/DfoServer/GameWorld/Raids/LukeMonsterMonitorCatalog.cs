using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Raids;

// 监控项顺序就是695/634列表顺序；PASSIVE是type4蓝条，不是普通怪物死亡来源。
internal sealed record LukeMonsterMonitor(int Template, bool Passive);
internal sealed record LukeMonsterMonitorCatalog(
    ImmutableDictionary<(int Dungeon, int Maze), ImmutableArray<LukeMonsterMonitor>> Targets,
    ImmutableArray<string> Issues)
{
    internal const string Path = "etc/raid/luke/luke.etc.buff";
    internal bool Ready => !Targets.IsEmpty && Issues.IsEmpty;
    internal ImmutableArray<LukeMonsterMonitor> Find(uint dungeon, int? maze) => !Ready ? [] :
        Targets.TryGetValue((checked((int)dungeon), -1), out var direct) ? direct :
        maze is { } ordinal ? Targets.GetValueOrDefault((checked((int)dungeon), ordinal), []) : [];

    internal static LukeMonsterMonitorCatalog Load(PvfArchive pvf, RaidDungeonCatalog dungeons)
    {
        if (!pvf.Contains(Path)) return new(ImmutableDictionary<(int, int), ImmutableArray<LukeMonsterMonitor>>.Empty,
            ["luke-monitor-file-missing"]);
        try
        {
            var catalog = Parse(pvf.ReadTokens(Path), pvf.ResolveString);
            // buff同时含未采用的普通入口；只绑定当前团本已登记地下城，不以额外声明开放副本。
            var bound = catalog.Targets.Where(row => dungeons.Find(row.Key.Dungeon) is not null).ToImmutableDictionary();
            return catalog with { Targets = bound, Issues = bound.IsEmpty ? ["luke-monitor-no-registered-dungeon"] : [] };
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException)
        {
            return new(ImmutableDictionary<(int, int), ImmutableArray<LukeMonsterMonitor>>.Empty,
                ["luke-monitor-file-invalid:" + error.GetType().Name]);
        }
    }

    internal static LukeMonsterMonitorCatalog Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        if (tokens.Count > 262144) throw new InvalidDataException("卢克监控声明超过读取预算");
        var document = new PvfTokenDocument(tokens, resolve);
        var lists = document.Sections.Where(row => row.Name == "raid monster list").ToArray();
        if (!document.Diagnostics.IsEmpty || lists is not [var list] || !list.IsBlock || !list.Values.IsEmpty ||
            list.Children.Length is < 1 or > 256)
            throw new InvalidDataException("卢克监控目录缺失、重复或结构不完整");
        var targets = ImmutableDictionary.CreateBuilder<(int, int), ImmutableArray<LukeMonsterMonitor>>();
        var dungeons = new HashSet<int>();
        foreach (var dungeon in list.Children)
        {
            if (dungeon is not { Name: "dungeon", IsBlock: true, Values: [{ Type: 0, Value: > 0 } id] } ||
                !dungeons.Add(id.Value) || dungeon.Children.IsEmpty)
                throw new InvalidDataException("卢克监控地下城重复或身份无效");
            if (dungeon.Children.All(row => row.Name == "maze"))
            {
                if (dungeon.Children.Length > 256) throw new InvalidDataException("卢克监控迷宫超过有界目录");
                foreach (var maze in dungeon.Children)
                {
                    if (maze is not { IsBlock: true, Values: [{ Type: 0, Value: >= 0 } ordinal] } ||
                        !targets.TryAdd((id.Value, ordinal.Value), ReadTargets(maze.Children)))
                        throw new InvalidDataException("卢克监控迷宫重复或身份无效");
                }
            }
            else targets.Add((id.Value, -1), ReadTargets(dungeon.Children));
        }
        return new(targets.ToImmutable(), []);

        static ImmutableArray<LukeMonsterMonitor> ReadTargets(ImmutableArray<PvfTokenSection> rows)
        {
            if (rows.Length is < 1 or > 255) throw new InvalidDataException("卢克监控项数量无法编码695");
            var result = ImmutableArray.CreateBuilder<LukeMonsterMonitor>(rows.Length);
            foreach (var row in rows)
            {
                if (row.IsBlock || !row.Children.IsEmpty || row.Name is not ("named" or "boss" or "passive") ||
                    row.Values is not [{ Type: 0, Value: > 0 } template])
                    throw new InvalidDataException("卢克监控项类型、数量或模板无效");
                result.Add(new(template.Value, row.Name == "passive"));
            }
            return result.MoveToImmutable();
        }
    }
}
