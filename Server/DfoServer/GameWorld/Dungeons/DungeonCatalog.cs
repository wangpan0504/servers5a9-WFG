using System.Collections.Concurrent;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal readonly record struct DungeonPoint(int X, int Y);
internal sealed record DungeonMap(string Kind, DungeonPoint Position, int Id, string Path);
internal sealed record DungeonRawSection(string Tag, IReadOnlyList<PvfToken> Tokens);
internal sealed record DungeonMaze(
    int SourceOrdinal,
    int Width,
    int Height,
    string Greed,
    IReadOnlyList<DungeonPoint> Starts,
    IReadOnlyList<DungeonPoint> Bosses,
    IReadOnlyList<DungeonMap>? MapSpecification,
    IReadOnlyList<PvfToken> QuestConnectionTokens)
{
    // 仅识别已确认的三个整数；原始 token 继续保留给资源诊断。
    public bool ConnectsQuest(uint questId) => QuestConnectionTokens.Count == 3 &&
        QuestConnectionTokens.All(token => token.Type == 0) && QuestConnectionTokens[0].Value == 0 &&
        QuestConnectionTokens[1].Value == questId && QuestConnectionTokens[2].Value == -1;

    // 只描述资源结构，不表示准入通过或可以向客户端发布初始化通知。
    public string MapStructure => MapSpecification is null ? "unsupported" : "explicit";
    public string? UnsupportedReason => MapSpecification is null ? "缺少显式 map specification，地图生成合同未实现" : null;
    // 资源查询及inspect JSON提供整数索引；未知格式不会阻断其他maze或生成已批准通路。
    public DungeonGreedGrid GreedGrid => DungeonGreedGrid.Read(Width, Height, Greed);
    public DungeonStaticTopology StaticTopology => DungeonStaticTopology.Read(this);
}

internal sealed record DungeonDefinition(int Id, string Path, int? MinimumLevel, int? BasisLevel, IReadOnlyList<DungeonMaze> Mazes, IReadOnlyList<DungeonRawSection> RawSections);
internal sealed class DungeonCatalog
{
    private readonly PvfArchive archive;
    private readonly IReadOnlyDictionary<int, string> dungeons, maps;
    private readonly ConcurrentDictionary<int, Lazy<DungeonDefinition>> definitions = new();
    private DungeonCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> dungeons, IReadOnlyDictionary<int, string> maps) => (this.archive, this.dungeons, this.maps) = (archive, dungeons, maps);
    public int DungeonCount => dungeons.Count;

    public static DungeonCatalog Load(PvfArchive pvf)
    {

        // 这里只加载两个目录；未知副本的脚本不会因加载目录而被强制解析。
        var dungeons = ParseDirectory(pvf.ReadTokens("dungeon/dungeon.lst"), pvf.ResolveString, "dungeon");
        var maps = ParseDirectory(pvf.ReadTokens("map/map.lst"), pvf.ResolveString, "map");
        return new(pvf, dungeons, maps);
    }

    public DungeonDefinition Get(int id)
    {
        if (id < 0 || !dungeons.TryGetValue(id, out var path))
            throw new InvalidDataException("副本索引不存在: " + id);
        // Lazy 在完整解析成功后才发布 Definition；失败缓存为异常，不发布局部 maze。
        return definitions.GetOrAdd(id, _ => new(() => ParseResource(id, path, archive.ReadTokens(path), archive.ResolveString, maps, archive.Contains), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    internal static IReadOnlyDictionary<int, string> ParseDirectory(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, string root)
    {
        if (tokens.Count == 0 || tokens.Count % 2 != 0)
            throw new InvalidDataException("PVF 目录记录不完整: " + root);
        var result = new Dictionary<int, string>();
        for (int i = 0; i < tokens.Count; i += 2)
        {
            if (tokens[i].Type != 0 || tokens[i].Value < 0 || tokens[i + 1].Type != 6)
                throw new InvalidDataException("PVF 目录 ID/路径类型无效: " + root);
            string path = PvfArchive.NormalizePath(root + "/" + resolve(tokens[i + 1].Value));
            if (!path.StartsWith(root + "/", StringComparison.Ordinal) || path.Length == root.Length + 1)
                throw new InvalidDataException("PVF 目录路径越过资源根: " + root);
            if (!result.TryAdd(tokens[i].Value, path))
                throw new InvalidDataException("重复 PVF 目录 ID: " + tokens[i].Value);
        }

        return result.AsReadOnly();
    }

    internal static DungeonDefinition ParseResource(int id, string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, IReadOnlyDictionary<int, string> mapPaths, Func<string, bool> exists)
    {
        if (id < 0)
            throw new InvalidDataException("副本索引不能为负");
        var sections = Sections(tokens, resolve);
        int? minimum = Scalar("minimum required level"), basis = Scalar("basis level");
        var mazes = new List<DungeonMaze>();
        Dictionary<string, DungeonRawSection>? current = null;
        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            if (section.Tag == "maze info")
            {
                if (section.Tokens.Count != 0)
                    throw new InvalidDataException("maze info 标记带有未知参数");
                Finish();
                current = new(StringComparer.Ordinal);
                continue;
            }

            if (section.Tag == "/maze info")
            {
                Finish();
                continue;
            }

            if (current is null || section.Tag.StartsWith('/'))
                continue;
            if (section.Tag is not ("size" or "greed" or "map specification" or "start map" or "boss map" or "quest connection"))
                continue;
            if (!current.TryAdd(section.Tag, section))
                throw new InvalidDataException("重复 maze 字段: " + section.Tag);
            if (section.Tag is "map specification" or "start map" or "boss map")
            {
                if (i + 1 >= sections.Count || sections[i + 1].Tag != "/" + section.Tag || sections[i + 1].Tokens.Count != 0)
                    throw new InvalidDataException("maze 块未正常结束: " + section.Tag);
            }
        }

        Finish();
        if (mazes.Count == 0)
            throw new NotSupportedException("unsupported: 未发现 maze info 资源结构");
        return new(id, path, minimum, basis, Freeze(mazes), Freeze(sections));
        int? Scalar(string tag)
        {
            var matches = sections.Where(s => s.Tag == tag).ToArray();
            if (matches.Length == 0)
                return null;
            if (matches.Length != 1 || matches[0].Tokens.Count != 1 || matches[0].Tokens[0].Type != 0 || matches[0].Tokens[0].Value < 0)
                throw new InvalidDataException("副本等级字段无效或重复: " + tag);
            return matches[0].Tokens[0].Value;
        }

        void Finish()
        {
            if (current is null)
                return;
            mazes.Add(ParseMaze(mazes.Count, current, resolve, mapPaths, exists));
            current = null;
        }
    }

    private static DungeonMaze ParseMaze(int ordinal, IReadOnlyDictionary<string, DungeonRawSection> sections, Func<int, string> resolve, IReadOnlyDictionary<int, string> mapPaths, Func<string, bool> exists)
    {
        var size = Integers(Required("size"));
        if (size.Length != 2 || size[0] <= 0 || size[1] <= 0)
            throw new InvalidDataException("maze size 必须是两个正整数");
        int width = size[0], height = size[1];
        var greedTokens = Required("greed");
        if (greedTokens.Count != 1 || greedTokens[0].Type != 6)
            throw new NotSupportedException("unsupported: greed 不是单个字符串");
        string greed = resolve(greedTokens[0].Value);
        // 保留原文；类型化GreedGrid独立返回不支持原因，未知编码不使整个副本定义失败。
        var starts = Points(Required("start map"));
        var bosses = Points(Required("boss map"));
        IReadOnlyList<DungeonMap>? specification = null;
        if (sections.TryGetValue("map specification", out var mapSection))
        {
            var values = mapSection.Tokens;
            if (values.Count == 0 || values.Count % 4 != 0)
                throw new InvalidDataException("map specification 记录不完整");
            var items = new List<DungeonMap>();
            var positions = new HashSet<DungeonPoint>();
            for (int i = 0; i < values.Count; i += 4)
            {
                if (values[i].Type != 6)
                    throw new NotSupportedException("unsupported: map specification 的类型标记未实现");
                string kind = resolve(values[i].Value);
                if (kind is not ("map" or "boss"))
                    throw new NotSupportedException("unsupported: map specification 类型 " + kind);
                int[] numbers = Integers(values.Skip(i + 1).Take(3).ToArray());
                var position = new DungeonPoint(numbers[0], numbers[1]);
                ValidatePoint(position);
                if (!positions.Add(position))
                    throw new InvalidDataException("重复 map specification 坐标");
                if (numbers[2] < 0 || !mapPaths.TryGetValue(numbers[2], out var mapPath) || !exists(mapPath))
                    throw new InvalidDataException("map specification 引用的地图不存在: " + numbers[2]);
                items.Add(new(kind, position, numbers[2], mapPath));
            }

            if (starts.Any(p => !positions.Contains(p)) || bosses.Any(p => !items.Any(m => m.Position == p && m.Kind == "boss")))
                throw new InvalidDataException("start/boss map 没有对应的 map specification");
            specification = Freeze(items);
        }

        var quest = sections.TryGetValue("quest connection", out var connection) ? connection.Tokens : Freeze(Array.Empty<PvfToken>());
        if (connection is not null && quest.Count == 0)
            throw new InvalidDataException("quest connection 不能为空");
        return new(ordinal, width, height, greed, starts, bosses, specification, quest);
        IReadOnlyList<PvfToken> Required(string tag) => sections.TryGetValue(tag, out var value) ? value.Tokens : throw new InvalidDataException("maze 缺少字段: " + tag);
        IReadOnlyList<DungeonPoint> Points(IReadOnlyList<PvfToken> values)
        {
            int[] numbers = Integers(values);
            if (numbers.Length == 0 || numbers.Length % 2 != 0)
                throw new InvalidDataException("start/boss map 坐标不完整");
            var result = new List<DungeonPoint>();
            var seen = new HashSet<DungeonPoint>();
            for (int i = 0; i < numbers.Length; i += 2)
            {
                var point = new DungeonPoint(numbers[i], numbers[i + 1]);
                ValidatePoint(point);
                if (!seen.Add(point))
                    throw new InvalidDataException("重复 start/boss map 坐标");
                result.Add(point);
            }

            return Freeze(result);
        }

        void ValidatePoint(DungeonPoint point)
        {
            if (point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height)
                throw new InvalidDataException("maze 坐标超出 size");
        }
    }

    private static int[] Integers(IReadOnlyList<PvfToken> tokens)
    {
        if (tokens.Any(t => t.Type != 0))
            throw new NotSupportedException("unsupported: maze 数值包含未实现的 token 类型");
        return tokens.Select(t => t.Value).ToArray();
    }

    private static IReadOnlyList<DungeonRawSection> Sections(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var sections = new List<DungeonRawSection>();
        for (int i = 0; i < tokens.Count;)
        {
            if (tokens[i].Type != 3)
                throw new InvalidDataException("副本脚本存在无标签 token");
            string raw = resolve(tokens[i++].Value);
            if (!raw.StartsWith('[') || !raw.EndsWith(']') || raw.Length < 3)
                throw new InvalidDataException("副本脚本标签无效");
            string tag = raw[1..^1].Trim().ToLowerInvariant();
            int start = i;
            while (i < tokens.Count && tokens[i].Type != 3)
                i++;
            sections.Add(new(tag, Freeze(tokens.Skip(start).Take(i - start))));
        }

        return Freeze(sections);
    }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
}
