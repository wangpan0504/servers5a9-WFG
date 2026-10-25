using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 03FB9D50/03FB9DC0分别读取日、周显示上限；重置键、标志和组长度保留原值。
internal sealed record DungeonEnterCountLimit(int ResetKey, int Daily, int Flag, int Weekly, int GroupSize);

internal sealed class DungeonEnterCountCatalog
{
    public const string ResourcePath = "etc/dungeonetc/dungeonincountinfo.etc";
    public IReadOnlyDictionary<int, DungeonEnterCountLimit> Limits { get; }

    DungeonEnterCountCatalog(Dictionary<int, DungeonEnterCountLimit> limits) => Limits = limits.AsReadOnly();

    public static DungeonEnterCountCatalog Load(PvfArchive archive) => Read(archive.ReadTokens(ResourcePath), archive.ResolveString);

    internal static DungeonEnterCountCatalog Read(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var result = new Dictionary<int, DungeonEnterCountLimit>();
        bool found = false;
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3 || resolve(tokens[at].Value) != "[dungeon incount info]") continue;
            if (found) throw new InvalidDataException("副本角色次数表重复");
            found = true;
            var values = new List<int>();
            while (++at < tokens.Count && tokens[at].Type != 3)
            {
                if (tokens[at].Type != 0) throw new InvalidDataException("副本角色次数表包含非整数");
                values.Add(tokens[at].Value);
            }
            if (at == tokens.Count || resolve(tokens[at].Value) != "[/dungeon incount info]")
                throw new InvalidDataException("副本角色次数表未闭合");
            for (int row = 0; row < values.Count;)
            {
                if (values.Count - row < 2) throw new InvalidDataException("副本角色次数行不完整");
                int key = values[row++], count = values[row++];
                if (key < 0 || count <= 0 || count > values.Count - row - 3)
                    throw new InvalidDataException("副本角色次数名册长度无效");
                int end = row + count;
                var limit = new DungeonEnterCountLimit(key, values[end], values[end + 1], values[end + 2], count);
                if (limit.Daily is < -1 or > byte.MaxValue || limit.Weekly is < -1 or > byte.MaxValue)
                    throw new InvalidDataException("副本角色次数上限超出通知范围");
                while (row < end)
                {
                    int id = values[row++];
                    if (id <= 0 || !result.TryAdd(id, limit)) throw new InvalidDataException("副本角色次数编号无效或重复");
                }
                row += 3;
            }
        }
        if (!found || result.Count == 0) throw new InvalidDataException("缺少副本角色次数表");
        return new(result);
    }
}
