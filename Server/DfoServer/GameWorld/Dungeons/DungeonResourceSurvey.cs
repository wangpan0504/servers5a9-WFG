using System.Buffers.Binary;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonFileSurvey(string Path, int DataType, int Size, IReadOnlyList<string> Tags, IReadOnlyList<byte> TokenTypes, string? Error);
internal sealed record DungeonGreedSurvey(int SourceOrdinal, string Format, int? CellCount, string? UnsupportedReason);
internal sealed record DungeonDefinitionSurvey(int Id, string Path, int? MazeCount, int? ExplicitMazeCount, string? Error)
{
    public IReadOnlyList<DungeonGreedSurvey> GreedGrids { get; init; } = Array.AsReadOnly(Array.Empty<DungeonGreedSurvey>());
}
internal sealed record DungeonMapSurvey(int Id, string Path, int? MonsterRows, int? PassiveObjectRows, int? BuffValues, string? Error);
internal sealed record DungeonTagSurvey(string Root, string Tag, int FileCount);
internal sealed record DungeonSurvey(string Scope, string Evidence,
    IReadOnlyList<DungeonFileSurvey> Files, IReadOnlyList<DungeonDefinitionSurvey> Dungeons,
    IReadOnlyList<DungeonMapSurvey> Maps, IReadOnlyList<DungeonTagSurvey> Tags);

internal static class DungeonResourceSurvey
{
    private static readonly string[] Roots = ["dungeon", "map", "monster", "passiveobject"];

    public static DungeonSurvey Scan(PvfArchive archive)
    {
        // 扫描归档中的全部领域文件，包含目录未引用的脚本；单条失败不吞掉其余资源。
        var files = new List<DungeonFileSurvey>();
        var tagNames = new Dictionary<int, string>();
        var tagCounts = new Dictionary<(string Root, string Tag), int>();
        archive.ScanRawFiles(archive.Files.Where(file => Root(file.Path) is not null), (file, read) =>
        {
            var inspected = InspectFile(file, read, ResolveTag);
            files.Add(inspected);
            string root = Root(file.Path)!;
            // 标签文本按字符串池ID共享，统计只留计数，避免百万文件的标签明细被GroupBy再次保留。
            foreach (string tag in inspected.Tags)
                tagCounts[(root, tag)] = tagCounts.GetValueOrDefault((root, tag)) + 1;
        });
        files.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        var tags = tagCounts.OrderBy(pair => pair.Key.Root, StringComparer.Ordinal).ThenBy(pair => pair.Key.Tag, StringComparer.Ordinal)
            .Select(pair => new DungeonTagSurvey(pair.Key.Root, pair.Key.Tag, pair.Value)).ToArray();

        var catalog = DungeonCatalog.Load(archive);
        var dungeons = DungeonCatalog.ParseDirectory(archive.ReadTokens("dungeon/dungeon.lst"), archive.ResolveString, "dungeon");
        var dungeonResults = new List<DungeonDefinitionSurvey>();
        foreach (var entry in dungeons.OrderBy(entry => entry.Key))
        {
            try
            {
                var definition = catalog.Get(entry.Key);
                dungeonResults.Add(new(entry.Key, entry.Value, definition.Mazes.Count,
                    definition.Mazes.Count(maze => maze.MapSpecification is not null), null)
                {
                    GreedGrids = Freeze(definition.Mazes.Select(maze =>
                    {
                        DungeonGreedGrid grid = maze.GreedGrid;
                        return new DungeonGreedSurvey(maze.SourceOrdinal, grid.Format, grid.Cells?.Count, grid.UnsupportedReason);
                    }))
                });
            }
            catch (Exception error) when (IsResourceFailure(error))
            {
                dungeonResults.Add(new(entry.Key, entry.Value, null, null, error.Message));
            }
        }

        var rooms = DungeonRoomCatalog.Load(archive);
        var maps = DungeonCatalog.ParseDirectory(archive.ReadTokens("map/map.lst"), archive.ResolveString, "map");
        var mapResults = new List<DungeonMapSurvey>();
        foreach (var entry in maps.OrderBy(entry => entry.Key))
        {
            try
            {
                // 目录地图检查不选择迷宫或运行坐标；这里的坐标只满足资源值类型。
                var room = rooms.Get(new("map", new(0, 0), entry.Key, entry.Value));
                mapResults.Add(new(entry.Key, entry.Value, room.Monsters.Count, room.PassiveObjects.Count, room.Buffs.Count, null));
            }
            catch (Exception error) when (IsResourceFailure(error))
            {
                mapResults.Add(new(entry.Key, entry.Value, null, null, null, error.Message));
            }
        }

        return new(string.Join(',', Roots),
            "仅归档完整性、标签清单及现有资源解析；不证明机制语义、准入、战斗、掉落或客户端可玩",
            Freeze(files), Freeze(dungeonResults), Freeze(mapResults), Freeze(tags));

        string ResolveTag(int id)
        {
            if (!tagNames.TryGetValue(id, out string? text))
                tagNames[id] = text = archive.ResolveString(id);
            return text;
        }
    }

    private static string? Root(string path)
    {
        foreach (string root in Roots)
            if (path.Length > root.Length && path[root.Length] == '/' && path.StartsWith(root, StringComparison.Ordinal))
                return root;
        return null;
    }

    internal static DungeonFileSurvey InspectFile(PvfFile file, Func<byte[]> read, Func<int, string> resolve)
    {
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        var types = new SortedSet<byte>();
        try
        {
            byte[] raw = read();
            if (raw.Length != file.Size)
                throw new InvalidDataException("资源长度与归档目录不符");
            if (file.DataType == 1)
            {
                if (raw.Length % 5 != 0)
                    throw new InvalidDataException("PVF token 流长度不完整");
                for (int at = 0; at < raw.Length; at += 5)
                {
                    byte type = raw[at];
                    types.Add(type);
                    if (type == 3)
                        tags.Add(resolve(BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at + 1, 4))));
                }
            }
            return new(file.Path, file.DataType, file.Size, Freeze(tags), Freeze(types), null);
        }
        catch (Exception error) when (IsResourceFailure(error))
        {
            // 出错条目不发布局部标签，防止把未读完的脚本当作已完整扫描。
            return new(file.Path, file.DataType, file.Size, Freeze(Array.Empty<string>()), Freeze(Array.Empty<byte>()), error.Message);
        }
    }

    private static bool IsResourceFailure(Exception error) => error is InvalidDataException or FileNotFoundException or NotSupportedException;
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
}
