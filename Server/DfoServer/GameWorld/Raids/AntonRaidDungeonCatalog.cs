using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer.GameWorld.Raids;

// 安徒恩目录与普通目录隔离；DGN来源为PVF明确的tutorial项，MAP必须来自调用者的显式登记。
internal sealed class AntonRaidDungeonCatalog(RaidScript script, ImmutableDictionary<int, DungeonDefinition> definitions,
    ImmutableArray<int> missingMaps, IReadOnlyDictionary<int, string> maps, Func<string, bool> exists)
{
    public RaidScript Script { get; } = script;
    public ImmutableArray<int> MissingMaps { get; } = missingMaps;
    public bool Ready => Script.Issues.IsEmpty && MissingMaps.IsEmpty &&
        Script.Phases.SelectMany(phase => phase.Dungeons).All(row => definitions.ContainsKey(row.Id));
    public DungeonDefinition? Find(int id) => definitions.GetValueOrDefault(id);
    public DungeonMaze ResolveMaze(DungeonMaze maze, Func<int, int>? draw) => DungeonCatalog.ResolveMapCandidates(maze,
        maps, exists, draw ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32);

    public static AntonRaidDungeonCatalog Load(PvfArchive pvf, RaidResources resources, DungeonCatalog main) =>
        Load(pvf, resources, main.MapPath);

    // 显式映射也供资源修复工具校验；不扫描文件名，不将缺项补成0或按DGN编号拼路径。
    internal static AntonRaidDungeonCatalog Load(PvfArchive pvf, RaidResources resources, Func<int, string?> mapPath)
    {
        var maps = new Dictionary<int, string>();
        var missing = ImmutableArray.CreateBuilder<int>();
        foreach (int id in resources.Dungeons.SelectMany(row => row.MapIds).Distinct().Order())
        {
            string? path = mapPath(id);
            if (path is null || !path.StartsWith("map/", StringComparison.Ordinal) || !pvf.Contains(path)) missing.Add(id);
            else maps.Add(id, path);
        }
        var entries = ImmutableDictionary.CreateBuilder<int, DungeonDefinition>();
        foreach (var row in resources.Dungeons)
        {
            string? path = row.FormalFileExists ? row.FormalPath : row.TutorialFileExists ? row.TutorialPath : null;
            if (path is null || row.MapIds.Any(id => !maps.ContainsKey(id))) continue;
            var definition = DungeonCatalog.ParseResource(row.Id, path, pvf.ReadTokens(path), pvf.ResolveString, maps, pvf.Contains);
            // 正式定义本身须带raid标记；普通安徒恩的raid normal标记不能借用正式授权。
            if (!definition.RawSections.Any(section => section.Tag == "raid dungeon" && section.Tokens is [{ Type: 0, Value: 1 }]) ||
                !definition.Path.StartsWith("dungeon/anton/", StringComparison.Ordinal)) continue;
            entries.Add(row.Id, definition);
        }
        return new(resources.Script, entries.ToImmutable(), missing.ToImmutable(), maps.ToImmutableDictionary(), pvf.Contains);
    }
}
