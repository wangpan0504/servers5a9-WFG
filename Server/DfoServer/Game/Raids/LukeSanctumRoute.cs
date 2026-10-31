using System.Collections.Immutable;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 全部资源三元组在装配时核验；每团只冻结一个组，通关步序由原Battle推进。
// 组等概率选择是本服兼容策略，不声称复原了原服的随机分布。
internal sealed class LukeSanctumRoute(ImmutableArray<LukeFixedCardRoute> groups)
{
    public int GroupCount => groups.Length;
    public string Fingerprint => groups[0].Options.ResourceFingerprint;
    public int? Maze(int group, int dungeon, int step) => group >= 0 && group < groups.Length ? groups[group].Maze(dungeon, step) : null;
    public bool Matches(RaidScript script) => groups.All(group => group.Matches(script));
    public bool Matches(RaidDungeonCatalog catalog) => groups.All(group => group.Matches(catalog));
    public bool MatchesObjects(LukeRaidObjectCatalog? objects) => groups.All(group => group.MatchesObjects(objects));

    internal static IReadOnlyList<(int Dungeon, int MazeOrdinal)> ResourceChoices(RaidDungeonCatalog catalog)
    {
        if (catalog.Type != 1 || catalog.Script.LukeCardMapRecords.Length != 5)
            throw new InvalidDataException("卢克三圣所资源组数量不匹配");
        var choices = new List<(int Dungeon, int MazeOrdinal)>();
        for (int group = 0; group < catalog.Script.LukeCardMapRecords.Length; group++)
        {
            int[] columns = catalog.LukeCardMapColumns(group);
            for (int column = 0; column < 3; column++)
                for (int step = 0; step < 3; step++)
                {
                    var choice = (Dungeon: 5054 + column, MazeOrdinal: columns[column] + step);
                    if (choices.Contains(choice) || catalog.Find(choice.Dungeon)?.Mazes.Count(maze => maze.SourceOrdinal == choice.MazeOrdinal) != 1)
                        throw new InvalidDataException("卢克三圣所三元组重叠或超出实际maze范围");
                    choices.Add(choice);
                }
        }
        return choices;
    }

    public static LukeSanctumRoute Create(RaidDungeonCatalog catalog, LukeRaidObjectCatalog? objects)
    {
        _ = ResourceChoices(catalog);
        string fingerprint = LukeFixedCardRoute.Fingerprint(catalog);
        var result = ImmutableArray.CreateBuilder<LukeFixedCardRoute>();
        for (int group = 0; group < catalog.Script.LukeCardMapRecords.Length; group++)
        {
            var options = new LukeFixedCardRouteOptions("LukeExplicitCardRoutePolicyV1", fingerprint, group,
                [5054, 5055, 5056], [0, 1, 2], 3, LukeRouteFailure.RetrySameStep, LukeRouteReset.ResetStepKeepGroup);
            result.Add(LukeFixedCardRoute.Create(options, catalog, objects, out var rejection)
                ?? throw new InvalidDataException(rejection));
        }
        return new(result.ToImmutable());
    }

    internal static LukeRaidStartPolicy? Resolve(LukeRaidStartPolicy? policy, RaidDefinition? definition)
    {
        if (policy?.Run is not { UseResourceCardRoute: true } run) return policy;
        if (run.CardRoute is not null || run.FixedRoute is not null || run.FixedRouteOptions is not null ||
            definition?.Resources?.EntryCatalog is not { } catalog)
            throw new InvalidDataException("卢克动态三圣所路线配置冲突或缺少资源目录");
        var route = Create(catalog, definition.Resources.LukeObjects);
        if (!route.Matches(policy.ExecutionScript)) throw new InvalidDataException("卢克动态三圣所执行脚本不匹配");
        return policy with { Run = run with { UseResourceCardRoute = false, CardRoute = route } };
    }
}
