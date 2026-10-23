using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal readonly record struct DungeonSpecificCardItem(int ItemId, int Weight, int Count);

// 消费已接入的普通安徒恩、黎明裂缝和普通超时空默认行；非零选择字段不作推断。
internal static class DungeonSpecificCardRewards
{
    public static ImmutableDictionary<int, ImmutableArray<DungeonSpecificCardItem>> Parse(
        IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var result = new Dictionary<int, List<DungeonSpecificCardItem>>();
        bool inside = false, seen = false;
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3) continue;
            string tag = resolve(tokens[at].Value).ToLowerInvariant();
            if (tag == "[dungeon_index specific reward rate]")
            {
                if (seen) throw new InvalidDataException("副本专用翻牌奖励表重复");
                seen = inside = true;
            }
            else if (tag == "[/dungeon_index specific reward rate]") inside = false;
            else if (inside && tag == "[dungeon type]" && at + 1 < tokens.Count && tokens[at + 1] is { Type: 0, Value: 45 or 72 or 75 })
            {
                int start = at + 2;
                at = start;
                while (at < tokens.Count && tokens[at].Type != 3) at++;
                if ((at - start) % 5 != 0 || at == start || at >= tokens.Count ||
                    resolve(tokens[at].Value).ToLowerInvariant() != "[/dungeon type]")
                    throw new InvalidDataException("副本专用翻牌奖励必须是完整五整数行");
                for (int row = start; row < at; row += 5)
                {
                    if (Enumerable.Range(row, 5).Any(index => tokens[index].Type != 0) ||
                        tokens[row].Value <= 0 || tokens[row + 1].Value != 0 || tokens[row + 2].Value <= 0 ||
                        tokens[row + 3].Value is <= 0 or > 10000 || tokens[row + 4].Value <= 0)
                        throw new NotSupportedException("副本专用翻牌奖励身份、选择字段或权重数量不支持");
                    int dungeon = tokens[row].Value;
                    if (!result.TryGetValue(dungeon, out var items)) result.Add(dungeon, items = []);
                    items.Add(new(tokens[row + 2].Value, tokens[row + 3].Value, tokens[row + 4].Value));
                }
            }
        }
        if (inside) throw new InvalidDataException("副本专用翻牌奖励表未闭合");
        if (result.Values.Any(items => items.Sum(item => (long)item.Weight) != 10000))
            throw new InvalidDataException("副本专用翻牌奖励权重总和必须为10000");
        return result.ToImmutableDictionary(row => row.Key, row => row.Value.ToImmutableArray());
    }
}
