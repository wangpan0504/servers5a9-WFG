using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Raids;

internal enum LukeRouteFailure { RetrySameStep }
internal enum LukeRouteReset { ResetStepKeepGroup }
// 本服显式兼容配置；不是原服随机算法。三列绑定和三个偏移均由调用者明确给定。
internal sealed record LukeFixedCardRouteOptions(string Version, string ResourceFingerprint, int Group,
    ImmutableArray<int> ColumnDungeons, ImmutableArray<int> StepOffsets, int InitialHp,
    LukeRouteFailure Failure, LukeRouteReset Reset);
internal sealed record LukeRouteProgress(long Generation, int Step, long Revision, bool Invalid = false);
internal sealed record LukeRouteLease(Guid Battle, int Phase, int Dungeon, long Generation, long Revision,
    int Group, int Step, int MazeOrdinal, int ExpectedHp, string Fingerprint);

internal sealed record LukeRouteMazeInspection(int Dungeon, int MazeOrdinal, int? Hp, int Width, int Height,
    int Starts, int Bosses, int Rooms, int CoveredRooms, ImmutableArray<string> Problems);

// 只接受已核验目录与所有选中房间的设施覆盖；构造后不再解析资源或在锁内做散列。
internal sealed class LukeFixedCardRoute
{
    readonly RaidDungeonCatalog catalog;
    readonly LukeRaidObjectCatalog objects;
    readonly ImmutableDictionary<int, ImmutableArray<int>> ordinals;
    public LukeFixedCardRouteOptions Options { get; }
    public ImmutableArray<LukeRaidMazeChoice> Choices { get; }
    LukeFixedCardRoute(RaidDungeonCatalog catalog, LukeFixedCardRouteOptions options,
        ImmutableDictionary<int, ImmutableArray<int>> ordinals, LukeRaidObjectCatalog objects)
    {
        this.catalog = catalog; this.objects = objects; Options = options; this.ordinals = ordinals;
        Choices = ordinals.OrderBy(row => row.Key).SelectMany(row => row.Value.Select(ordinal => new LukeRaidMazeChoice(0, row.Key, ordinal))).ToImmutableArray();
    }
    public bool MatchesObjects(LukeRaidObjectCatalog? value) => ReferenceEquals(objects, value);
    public bool Matches(RaidDungeonCatalog value) => ReferenceEquals(catalog, value);
    public bool Matches(RaidScript script) => script.Phases.Length > 0 && Options.ColumnDungeons.All(id =>
        script.Phases[0].Dungeons.SingleOrDefault(row => row.Id == id) is { } row && row.HpRaw == Options.InitialHp &&
        row.HpSymbol == catalog.Script.Phases[0].Dungeons.Single(d => d.Id == id).HpSymbol);
    public int? Maze(int dungeon, int step) => ordinals.TryGetValue(dungeon, out var rows) && step >= 0 && step < rows.Length ? rows[step] : null;

    // DGN尾部可保留不属于当前maze的条件坐标。不存在的端点不会创建房间，原Run也不允许传送到它。
    // 两端均存在的实际传送仍需单独核验，不能因普通Greed连通就自动授予该捷径。
    static bool HasActiveWarps(DungeonMaze maze) => maze.MapSpecification is { } maps && maze.WarpMaps.Any(warp =>
        maps.Any(map => map.Position == warp.Source) && maps.Any(map => map.Position == warp.Destination));

    // 指纹只覆盖本策略解释的资源声明，不读取或散列整份PVF。
    public static string Fingerprint(RaidDungeonCatalog catalog)
    {
        var rows = new[] { 5054, 5055, 5056 }.Select(id => new { Id = id,
            Definition = catalog.Find(id) is not { } definition ? null : new { definition.Id, definition.Path,
                definition.MinimumLevel, definition.BasisLevel, definition.RawSections,
                Mazes = definition.Mazes.Select(maze => new { maze.SourceOrdinal, maze.Width, maze.Height, maze.Greed,
                    maze.Starts, maze.Bosses, maze.MapSpecification, maze.MapAlternatives, maze.QuestConnectionTokens,
                    maze.LayeredMaps, maze.StoryStages, maze.DummyStages, maze.ConditionalMaps, maze.WarpMaps,
                    maze.BossEntrance, maze.ClearConditions, maze.RandomizedObjects, maze.ResourceError }) }, Hp = catalog.Script.Phases.FirstOrDefault()?.Dungeons.SingleOrDefault(row => row.Id == id) });
        var text = JsonSerializer.Serialize(new { catalog.Script.LukeCardMapRecords, Dungeons = rows });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
    static bool ValidOptions(LukeFixedCardRouteOptions options, RaidDungeonCatalog catalog) =>
        !(options.Version != "LukeExplicitCardRoutePolicyV1" || options.InitialHp != 3 ||
            options.Failure != LukeRouteFailure.RetrySameStep || options.Reset != LukeRouteReset.ResetStepKeepGroup ||
            options.ColumnDungeons.IsDefault || options.ColumnDungeons.Length != 3 ||
            !options.ColumnDungeons.Order().SequenceEqual(new[] { 5054, 5055, 5056 }) ||
            options.StepOffsets.IsDefault || options.StepOffsets.Length != 3 || options.StepOffsets.Distinct().Count() != 3 ||
            options.StepOffsets.Any(offset => offset is < 0 or > 2) || catalog.Type != 1 || !catalog.MapsReady ||
            catalog.Script.Phases.IsDefaultOrEmpty || catalog.Script.LukeCardMapRecords.Length != 5 || options.Group is < 0 or > 4 ||
            string.IsNullOrWhiteSpace(options.ResourceFingerprint) || options.ResourceFingerprint != Fingerprint(catalog));

    // 仅给资源装配声明读取范围，不签发可执行策略或跳过随后全房间核验。
    internal static IReadOnlyList<(int Dungeon, int MazeOrdinal)> ResourceChoices(LukeFixedCardRouteOptions options, RaidDungeonCatalog catalog)
    {
        if (!ValidOptions(options, catalog)) throw new InvalidDataException("卢克Fixed资源配置或指纹不匹配");
        int[] columns = catalog.LukeCardMapColumns(options.Group);
        return options.ColumnDungeons.SelectMany((id, column) => options.StepOffsets.Select(offset =>
            (Dungeon: id, MazeOrdinal: columns[column] + offset))).ToArray();
    }

    // 只读完整九maze诊断。即使某项先失败也不提前停止，且不签发能力或弱化Create门槛。
    internal static ImmutableArray<LukeRouteMazeInspection> InspectResources(LukeFixedCardRouteOptions options,
        RaidDungeonCatalog catalog, LukeRaidObjectCatalog? objects)
    {
        var result = ImmutableArray.CreateBuilder<LukeRouteMazeInspection>();
        foreach (var (id, ordinal) in ResourceChoices(options,catalog))
        {
            var problems = ImmutableArray.CreateBuilder<string>();
            var definition = catalog.Find(id);
            int? hp = catalog.Script.Phases[0].Dungeons.SingleOrDefault(row=>row.Id==id)?.HpRaw;
            if (hp != 3) problems.Add("hp-not-three");
            if (definition?.Mazes.Count != 15) problems.Add("source-maze-count-not-fifteen");
            var matches = definition?.Mazes.Where(row=>row.SourceOrdinal==ordinal).ToArray();
            if (matches is not {Length:1})
            { result.Add(new(id,ordinal,hp,0,0,0,0,0,0,problems.ToImmutable().Add("source-ordinal-not-unique"))); continue; }
            var maze = matches[0];
            if (maze.QuestConnectionTokens.Count != 0) problems.Add("quest-connection");
            if (maze.ResourceError is { } resourceError) problems.Add("resource-error:"+resourceError);
            if (maze.Starts.Count != 1 || maze.Bosses.Count != 1) problems.Add("start-or-boss-count");
            if (maze.MapSpecification is null || maze.MapSpecification.Count == 0) problems.Add("map-specification-missing");
            if (maze.LayeredMaps.Count != 0 || maze.StoryStages.Count != 0 || maze.DummyStages.Count != 0 ||
                maze.ConditionalMaps.Count != 0 || HasActiveWarps(maze) || maze.BossEntrance is not null) problems.Add("unsupported-dynamic-map-source");
            try
            {
                var topology = catalog.ResolveMaze(maze,_=>0).StaticTopology;
                if (!topology.Supported) problems.Add("topology:"+topology.UnsupportedReason);
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException)
            { problems.Add("map-resource:"+error.GetType().Name+":"+error.Message); }
            int total = (maze.MapSpecification?.Count ?? 0) + maze.MapAlternatives.Count, covered = 0;
            if (total > 64) problems.Add("room-diagnostic-budget-exceeded");
            foreach (var map in (maze.MapSpecification ?? Array.Empty<DungeonMap>()).Concat(maze.MapAlternatives).Take(64))
            {
                if (map.IsConditional || map.LayerIndex >= 0 || map.StoryStageIndex >= 0 || map.DummyStageIndex >= 0 || map.CandidateIds.Count > 1)
                    problems.Add($"unsupported-map-identity:{map.Id}:{map.Kind}:{map.Position}");
                if (objects?.Find(new(id,ordinal,map.Kind,map.Position,map.Id,map.Path)) is not null) covered++;
                else problems.Add($"object-room-uncovered:{map.Id}:{map.Kind}:{map.Position}");
            }
            result.Add(new(id,ordinal,hp,maze.Width,maze.Height,maze.Starts.Count,maze.Bosses.Count,total,covered,problems.ToImmutable()));
        }
        return result.ToImmutable();
    }

    internal static LukeRaidStartPolicy? Resolve(LukeRaidStartPolicy? policy, RaidDefinition? definition)
    {
        if (policy?.Run?.FixedRouteOptions is not { } options) return policy;
        if (policy.Run.FixedRoute is not null || definition?.Resources?.EntryCatalog is not { } catalog)
            throw new InvalidDataException("卢克Fixed策略重复或缺少正式资源目录");
        var route = Create(options, catalog, definition.Resources.LukeObjects, out var rejection);
        if (route is null || !route.Matches(policy.ExecutionScript))
            throw new InvalidDataException(rejection ?? "卢克Fixed路线执行脚本不匹配");
        return policy with { Run = policy.Run with { FixedRouteOptions = null, FixedRoute = route } };
    }

    public static LukeFixedCardRoute? Create(LukeFixedCardRouteOptions options, RaidDungeonCatalog catalog,
        LukeRaidObjectCatalog? objects, out string? rejection)
    {
        rejection = "luke-fixed-route-policy-invalid";
        if (!ValidOptions(options, catalog)) return null;
        rejection = "luke-fixed-route-resource-unavailable";
        if (objects is null) { rejection = "luke-fixed-route-object-catalog-missing"; return null; }
        var blockingIssues = objects.BlockingRouteIssues;
        if (!blockingIssues.IsEmpty)
        {
            rejection = "luke-fixed-route-object-issues:" + string.Join(";", blockingIssues.Take(8));
            return null;
        }
        int[] columns = catalog.LukeCardMapColumns(options.Group);
        var result = ImmutableDictionary.CreateBuilder<int, ImmutableArray<int>>();
        for (int column = 0; column < 3; column++)
        {
            int id = options.ColumnDungeons[column];
            var definition = catalog.Find(id);
            rejection = $"luke-fixed-route-definition-or-hp:{id}";
            if (definition is null || definition.Mazes.Count != 15 ||
                catalog.Script.Phases[0].Dungeons.SingleOrDefault(row => row.Id == id) is not { HpRaw: 3 }) return null;
            var choices = options.StepOffsets.Select(offset => columns[column] + offset).ToImmutableArray();
            foreach (int ordinal in choices)
            {
                rejection = $"luke-fixed-route-maze-ordinal:{id}:{ordinal}";
                if (ordinal > byte.MaxValue || definition.Mazes.Count(row => row.SourceOrdinal == ordinal) != 1) return null;
                var maze = definition.Mazes.Single(row => row.SourceOrdinal == ordinal);
                rejection = $"luke-fixed-route-maze-shape:{id}:{ordinal}";
                if (maze.QuestConnectionTokens.Count != 0 || maze.ResourceError is not null || maze.MapSpecification is null ||
                    maze.MapSpecification.Count == 0 || maze.Starts.Count != 1 || maze.Bosses.Count != 1 ||
                    maze.LayeredMaps.Count != 0 || maze.StoryStages.Count != 0 || maze.DummyStages.Count != 0 ||
                    maze.ConditionalMaps.Count != 0 || HasActiveWarps(maze) || maze.BossEntrance is not null) return null;
                try
                {
                    var topology = catalog.ResolveMaze(maze, _ => 0).StaticTopology;
                    if (!topology.Supported)
                    { rejection = $"luke-fixed-route-topology:{id}:{ordinal}:{topology.UnsupportedReason}"; return null; }
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException)
                { rejection = $"luke-fixed-route-map-resource:{id}:{ordinal}:{error.GetType().Name}"; return null; }
                foreach (var map in maze.MapSpecification.Concat(maze.MapAlternatives))
                {
                    rejection = $"luke-fixed-route-object-room:{id}:{ordinal}:{map.Id}:{map.Kind}:{map.Position}";
                    if (map.IsConditional || map.LayerIndex >= 0 || map.StoryStageIndex >= 0 || map.DummyStageIndex >= 0 ||
                        map.CandidateIds.Count > 1 || objects.Find(new(id, ordinal, map.Kind, map.Position, map.Id, map.Path)) is null) return null;
                }
            }
            result.Add(id, choices);
        }
        rejection = null;
        return new(catalog, options, result.ToImmutable(), objects);
    }
}
