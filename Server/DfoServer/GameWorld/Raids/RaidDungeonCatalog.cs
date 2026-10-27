using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer.GameWorld.Raids;

// 团本目录与普通目录隔离；安徒恩保留明确tutorial备用项，MAP只取主表或应用显式传入的有证登记。
internal sealed class RaidDungeonCatalog(RaidScript script, ImmutableDictionary<int, DungeonDefinition> definitions,
    ImmutableArray<int> missingMaps, IReadOnlyDictionary<int, string> maps, Func<string, bool> exists, byte type = 0,
    ImmutableDictionary<int, string>? dungeonPaths = null)
{
    public byte Type { get; } = type;
    public RaidScript Script { get; } = script;
    public ImmutableArray<int> MissingMaps { get; } = missingMaps;
    // DGN路径登记与可执行Definition分开；缺MAP时仍保留已证tutorial登记，但Find继续拒绝进图。
    public ImmutableDictionary<int, string> DungeonPaths { get; } = dungeonPaths ?? ImmutableDictionary<int, string>.Empty;
    public ImmutableArray<int> MissingDungeons => Script.Phases.SelectMany(phase => phase.Dungeons).Select(row => row.Id)
        .Distinct().Where(id => !DungeonPaths.ContainsKey(id)).Order().ToImmutableArray();
    public bool Ready => Script.Issues.IsEmpty && MapsReady;
    public bool MapsReady => Type is 0 or 1 && MissingMaps.IsEmpty &&
        Script.Phases.SelectMany(phase => phase.Dungeons).All(row => definitions.ContainsKey(row.Id));
    public DungeonDefinition? Find(int id) => definitions.GetValueOrDefault(id);
    public DungeonMaze ResolveMaze(DungeonMaze maze, Func<int, int>? draw) => DungeonCatalog.ResolveMapCandidates(maze,
        maps, exists, draw ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32);

    public static RaidDungeonCatalog Load(PvfArchive pvf, RaidResources resources, DungeonCatalog main, byte type = 0) =>
        Load(pvf, resources, main.MapPath, type);

    // 显式映射也供资源修复工具校验；不扫描文件名，不将缺项补成0或按DGN编号拼路径。
    internal static RaidDungeonCatalog Load(PvfArchive pvf, RaidResources resources, Func<int, string?> mapPath, byte type = 0)
    {
        if (type is not (0 or 1)) throw new InvalidDataException("未登记团本目录类型");
        var maps = new Dictionary<int, string>();
        var missing = ImmutableArray.CreateBuilder<int>();
        foreach (int id in resources.Dungeons.SelectMany(row => row.MapIds).Distinct().Order())
        {
            string? path = mapPath(id);
            if (path is null || !path.StartsWith("map/", StringComparison.Ordinal) || !pvf.Contains(path)) missing.Add(id);
            else maps.Add(id, path);
        }
        var entries = ImmutableDictionary.CreateBuilder<int, DungeonDefinition>();
        var paths = ImmutableDictionary.CreateBuilder<int, string>();
        foreach (var row in resources.Dungeons)
        {
            string? path = row.FormalFileExists ? row.FormalPath : type == 0 && row.TutorialFileExists ? row.TutorialPath : null;
            if (path is null || !path.StartsWith(type == 0 ? "dungeon/anton/" : "dungeon/luke/", StringComparison.Ordinal)) continue;
            paths.Add(row.Id, path);
            if (row.MapIds.Any(id => !maps.ContainsKey(id))) continue;
            var definition = DungeonCatalog.ParseResource(row.Id, path, pvf.ReadTokens(path), pvf.ResolveString, maps, pvf.Contains);
            // 正式定义本身须带raid标记；普通安徒恩的raid normal标记不能借用正式授权。
            if (!definition.RawSections.Any(section => section.Tag == "raid dungeon" && section.Tokens is [{ Type: 0, Value: 1 }]) ||
                !definition.Path.StartsWith(type == 0 ? "dungeon/anton/" : "dungeon/luke/", StringComparison.Ordinal)) continue;
            entries.Add(row.Id, definition);
        }
        return new(resources.Script, entries.ToImmutable(), missing.ToImmutable(), maps.ToImmutableDictionary(), pvf.Contains, type, paths.ToImmutable());
    }
}
