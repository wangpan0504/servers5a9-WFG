using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal enum ClearRewardSectionUse
{
    ReadAlready,
    PartlyRead,
    NotGranted
}

// 专用翻牌及金币组按已接入消费者分类；未解释的特殊清图段不追加额外物品。
internal static class ClearRewardSections
{
    public const string Path = "etc/itemdropinfo_clearreward.etc";

    static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase)
    {
        "drop prob", "party member drop bonusrate", "reward item rate per map max count", "gold card cost table",
        "gold card blank item", "pcroom card blank item", "item drop rarity control", "chronicle set reward rate",
        "specific goldcard group", "reward group", "reward dungeon type list", "reward item list",
        "dungeon_index specific reward rate", "dungeon index privilege service reward rate", "special dungeon clear reward",
        "dungeon index", "clear reward", "special clear reward", "special clear reward item list", "static table", "table",
        "clear reward gold", "special clear reward item", "group", "dungeon", "reward item", "cardslot specific reward table",
        "dungeon type"
    };

    static readonly HashSet<string> Known = new(Containers, StringComparer.OrdinalIgnoreCase)
    {
        "drop prob count", "drop kind prob", "drop item type prob", "dungeon difficulty drop bonusrate",
        "basis of rarity dicision", "dungeon difficulty gold drop bonusrate", "gold card create rate",
        "item drop ref table", "mail message"
    };

    public static IReadOnlyList<int> ExtraItemGrants() => [];

    public static IReadOnlyList<(string Name, ClearRewardSectionUse Use)> Walk(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var rows = new List<(string, ClearRewardSectionUse)>();
        var stack = new Stack<string>();
        bool tayberrsGold = false;
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3) continue;
            string raw = resolve(tokens[at].Value).Trim().ToLowerInvariant();
            if (raw.Length < 3 || raw[0] != '[' || raw[^1] != ']')
                throw new InvalidDataException("通关奖励标签无法识别: " + raw);
            bool close = raw.StartsWith("[/", StringComparison.Ordinal);
            string name = close ? raw[2..^1].Trim() : raw[1..^1].Trim();
            if (!Known.Contains(name))
                throw new InvalidDataException("通关奖励标签未分类: " + name);
            if (close)
            {
                if (stack.Count == 0 || !string.Equals(stack.Pop(), name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("通关奖励标签未闭合: " + name);
                if (name == "reward group") tayberrsGold = false;
                continue;
            }
            if (name == "reward group" && stack.Contains("specific goldcard group") &&
                at + 1 < tokens.Count && tokens[at + 1] is { Type:0, Value:101 }) tayberrsGold = true;
            bool insideRead = tayberrsGold;
            foreach (string parent in stack)
                if (parent is "gold card cost table" or "dungeon index privilege service reward rate")
                    insideRead = true;
            bool specific = name == "dungeon type" && stack.Contains("dungeon_index specific reward rate") &&
                at + 1 < tokens.Count && tokens[at + 1] is { Type: 0, Value: 45 or 71 or 72 or 75 or 76 };
            var use = insideRead || specific ? ClearRewardSectionUse.ReadAlready
                : name is "gold card cost table" or "dungeon index privilege service reward rate"
                    ? ClearRewardSectionUse.ReadAlready : name is "dungeon_index specific reward rate" or "specific goldcard group"
                        ? ClearRewardSectionUse.PartlyRead : ClearRewardSectionUse.NotGranted;
            rows.Add((name, use));
            if (Containers.Contains(name)) stack.Push(name);
        }
        if (stack.Count != 0)
            throw new InvalidDataException("通关奖励段未闭合: " + stack.Peek());
        return rows;
    }
}
