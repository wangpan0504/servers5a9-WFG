using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record KnightShieldDefinition(int ItemId, byte GrowType, byte RequiredLevel, int ClearQuest);

internal sealed class KnightShieldCatalog
{
    public const string LegacyPath = "etc/character/knight/shieldwindowdata.etc";
    public const string CurrentPath = "etc/character/knight/shieldwindownewdata.etc";
    public string Path { get; }
    public ImmutableDictionary<int, KnightShieldDefinition> Definitions { get; }

    internal KnightShieldCatalog(string path, IEnumerable<KnightShieldDefinition> definitions)
    {
        Path = path;
        Definitions = definitions.ToImmutableDictionary(row => row.ItemId);
        if (Definitions.Count == 0 || Definitions.Values.Any(row => row.ItemId <= 0 || row.GrowType > 2 ||
            (row.RequiredLevel == 0) == (row.ClearQuest == 0) || row.ClearQuest < 0))
            throw new InvalidDataException("守护者盾牌资格目录不完整");
    }

    public static KnightShieldCatalog Load(PvfArchive archive, string path, ItemCatalog items)
    {
        if (path is not (LegacyPath or CurrentPath)) throw new InvalidDataException("未知守护者盾牌目录");
        var catalog = Parse(path, archive.ReadTokens(path), archive.ResolveString);
        foreach (var row in catalog.Definitions.Values)
        {
            var item = items.Get(row.ItemId);
            if (item.Kind != ItemKind.Equipment || item.EquipmentSlot != 24 || (item.Jobs & (1 << 12)) == 0 ||
                !item.Expiration.SupportsCreation)
                throw new InvalidDataException("守护者盾牌资格引用了无效支持装备: " + row.ItemId);
        }
        return catalog;
    }

    internal static KnightShieldCatalog Parse(string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var definitions = new List<KnightShieldDefinition>();
        Dictionary<string, PvfToken[]>? fields = null;
        for (int index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Type != 3) continue;
            string tag = resolve(tokens[index].Value);
            if (tag == "[shield]")
            {
                if (fields is not null) throw new InvalidDataException("盾牌声明未闭合");
                fields = new(StringComparer.Ordinal);
                continue;
            }
            if (tag == "[/shield]")
            {
                // 正式资源在第一项等级盾后保留一个孤立闭标签；只忽略该空闭合，不串联相邻条目。
                if (fields is null) continue;
                int Number(string name) => fields.TryGetValue(name, out var values) && values is [{ Type: 0 } value]
                    ? value.Value : throw new InvalidDataException("盾牌资格字段缺失或重复: " + name);
                string condition = fields.TryGetValue("[get condition]", out var values) && values is [{ Type: 6 } text]
                    ? resolve(text.Value) : path == LegacyPath ? "quest" : throw new InvalidDataException("盾牌获取条件缺失");
                int level = condition == "level" ? Number("[get shield level]") : 0;
                int quest = condition == "quest" ? Number("[clear quest index]") : 0;
                if (condition is not ("level" or "quest") || condition == "quest" && Number("[open quest index]") <= 0)
                    throw new InvalidDataException("盾牌获取条件未支持");
                definitions.Add(new(Number("[item index]"), checked((byte)Number("[grow type]")), checked((byte)level), quest));
                fields = null;
                continue;
            }
            if (fields is null) continue;
            int end = index + 1;
            while (end < tokens.Count && tokens[end].Type != 3) end++;
            if (!fields.TryAdd(tag, tokens.Skip(index + 1).Take(end - index - 1).ToArray()))
                throw new InvalidDataException("盾牌声明字段重复: " + tag);
            index = end - 1;
        }
        if (fields is not null) throw new InvalidDataException("盾牌声明未闭合");
        return new(path, definitions);
    }
}
