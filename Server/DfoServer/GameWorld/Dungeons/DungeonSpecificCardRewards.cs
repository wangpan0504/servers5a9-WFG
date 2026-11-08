using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal readonly record struct DungeonSpecificCardItem(int ItemId, int Weight, int Count);
internal readonly record struct SpecialDungeonClearRewardItem(int ItemId, int Probability, int Count);
internal readonly record struct SpecialDungeonClearRewardGrant(int ItemId, int Count);
internal sealed record SpecialDungeonClearReward(int DungeonType, string MailSender, string MailText,
    ImmutableArray<SpecialDungeonClearRewardItem> Items)
{
    public const int ProbabilityScale = 1_000_000;

    public bool Equals(SpecialDungeonClearReward? other) =>
        other is not null &&
        DungeonType == other.DungeonType &&
        string.Equals(MailSender, other.MailSender, StringComparison.Ordinal) &&
        string.Equals(MailText, other.MailText, StringComparison.Ordinal) &&
        !Items.IsDefault && !other.Items.IsDefault && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(DungeonType, MailSender, MailText, Items.IsDefault ? 0 : Items.Length);

    public ImmutableArray<SpecialDungeonClearRewardGrant> Roll(Func<int, int> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        var grants = ImmutableArray.CreateBuilder<SpecialDungeonClearRewardGrant>(Items.Length);
        foreach (var item in Items)
        {
            if (item.Probability >= ProbabilityScale)
            {
                grants.Add(new(item.ItemId, item.Count));
                continue;
            }
            int roll = draw(ProbabilityScale);
            if ((uint)roll >= ProbabilityScale)
                throw new InvalidDataException("特殊地下城通关奖励随机值超出百万分比范围");
            if (roll < item.Probability)
                grants.Add(new(item.ItemId, item.Count));
        }
        return grants.ToImmutable();
    }
}

// 消费已接入副本的默认行；非零选择字段不作推断。
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
            else if (inside && tag == "[dungeon type]" && at + 1 < tokens.Count && tokens[at + 1] is { Type: 0, Value: 45 or 71 or 72 or 75 or 76 or 91 })
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
    public static ImmutableArray<DungeonSpecificCardItem> ParseTayberrsPaid(IReadOnlyList<PvfToken> tokens, Func<int,string> resolve)
    {
        var result = ImmutableArray<DungeonSpecificCardItem>.Empty;
        bool inside = false;
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3) continue;
            string tag = resolve(tokens[at].Value).ToLowerInvariant();
            if (tag == "[specific goldcard group]") inside = true;
            else if (tag == "[/specific goldcard group]") inside = false;
            else if (inside && tag == "[reward group]" && at + 1 < tokens.Count && tokens[at + 1] is { Type:0, Value:101 })
            {
                if (!result.IsEmpty) throw new InvalidDataException("泰波尔斯金币奖励组重复");
                int[]? types = null, values = null;
                bool closed = false;
                for (at += 2; at < tokens.Count; at++)
                {
                    if (tokens[at].Type != 3) throw new InvalidDataException("金币奖励组含未知数据");
                    tag = resolve(tokens[at].Value).ToLowerInvariant();
                    if (tag == "[/reward group]") { closed = true; break; }
                    if (tag is not ("[reward dungeon type list]" or "[reward item list]"))
                        throw new NotSupportedException("金币奖励组含未知字段");
                    int start = ++at;
                    while (at < tokens.Count && tokens[at].Type == 0) at++;
                    string end = "[/" + tag[1..];
                    if (at >= tokens.Count || tokens[at].Type != 3 || resolve(tokens[at].Value).ToLowerInvariant() != end)
                        throw new InvalidDataException("金币奖励组字段未闭合");
                    int[] data = Enumerable.Range(start,at-start).Select(index => tokens[index].Value).ToArray();
                    if (tag == "[reward dungeon type list]" && types is null) types = data;
                    else if (tag == "[reward item list]" && values is null) values = data;
                    else throw new InvalidDataException("金币奖励组字段重复");
                }
                if (!closed || types is not [71] || values is null || values.Length == 0 || values.Length % 3 != 0)
                    throw new InvalidDataException("泰波尔斯金币奖励组类型或三列声明无效");
                var rows = ImmutableArray.CreateBuilder<DungeonSpecificCardItem>();
                for (int row = 0; row < values.Length; row += 3)
                {
                    if (values[row] <= 0 || values[row+1] <= 0 || values[row+2] <= 0)
                        throw new InvalidDataException("金币奖励物品、权重、数量必须为正整数");
                    rows.Add(new(values[row], values[row+1], values[row+2]));
                }
                if (rows.Sum(row => (long)row.Weight) != 10000)
                    throw new InvalidDataException("泰波尔斯金币奖励权重不完整");
                result = rows.ToImmutable();
            }
        }
        return result;
    }

    public static ImmutableDictionary<int, SpecialDungeonClearReward> ParseSpecialClearRewards(
        IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var result = new Dictionary<int, SpecialDungeonClearReward>();
        bool inside = false, seen = false;
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3) continue;
            string tag = resolve(tokens[at].Value).Trim().ToLowerInvariant();
            if (tag == "[special dungeon clear reward]")
            {
                if (seen) throw new InvalidDataException("特殊地下城通关奖励表重复");
                seen = inside = true;
            }
            else if (tag == "[/special dungeon clear reward]") inside = false;
            else if (inside)
            {
                if (tag != "[dungeon type]" || at + 1 >= tokens.Count || tokens[at + 1] is not { Type: 0, Value: 71 })
                    throw new NotSupportedException("特殊地下城通关奖励仅支持泰波尔斯类型71");
                int dungeonType = tokens[at + 1].Value;
                string? mailSender = null, mailText = null;
                bool closedType = false;
                for (at += 2; at < tokens.Count; at++)
                {
                    if (tokens[at].Type != 3) throw new InvalidDataException("特殊地下城通关奖励含未标记字段");
                    tag = resolve(tokens[at].Value).Trim().ToLowerInvariant();
                    if (tag == "[/dungeon type]") { closedType = true; break; }
                    if (tag == "[mail message]")
                    {
                        if (mailSender is not null || at + 2 >= tokens.Count ||
                            tokens[at + 1].Type != 6 || tokens[at + 2].Type != 6)
                            throw new InvalidDataException("特殊地下城通关奖励邮件文案声明无效");
                        mailSender = resolve(tokens[at + 1].Value).Trim();
                        mailText = resolve(tokens[at + 2].Value).Trim();
                        if (string.IsNullOrWhiteSpace(mailSender) || string.IsNullOrWhiteSpace(mailText))
                            throw new InvalidDataException("特殊地下城通关奖励邮件文案不能为空");
                        at += 2;
                        continue;
                    }
                    if (tag != "[dungeon index]" || at + 1 >= tokens.Count || tokens[at + 1] is not { Type: 0, Value: > 0 })
                        throw new InvalidDataException("特殊地下城通关奖励副本索引无效");
                    if (mailSender is null || mailText is null)
                        throw new InvalidDataException("特殊地下城通关奖励必须在副本行前声明邮件文案");
                    int dungeonId = tokens[at + 1].Value;
                    ImmutableArray<SpecialDungeonClearRewardItem> clearRewardItems = default;
                    bool closedIndex = false;
                    for (at += 2; at < tokens.Count; at++)
                    {
                        if (tokens[at].Type != 3)
                            throw new InvalidDataException("特殊地下城通关奖励副本段含未标记字段");
                        string childTag = resolve(tokens[at].Value).Trim().ToLowerInvariant();
                        if (childTag == "[/dungeon index]")
                        {
                            closedIndex = true;
                            break;
                        }
                        if (childTag == "[clear reward]")
                        {
                            if (!clearRewardItems.IsDefault)
                                throw new InvalidDataException("特殊地下城通关奖励clear reward重复: " + dungeonId);
                            int start = at + 1;
                            at = start;
                            while (at < tokens.Count && tokens[at].Type == 0) at++;
                            if (at == start || (at - start) % 3 != 0 || at >= tokens.Count ||
                                tokens[at].Type != 3 || resolve(tokens[at].Value).Trim().ToLowerInvariant() != "[/clear reward]")
                                throw new InvalidDataException("特殊地下城通关奖励clear reward须为完整三元组并正确闭合");
                            var items = ImmutableArray.CreateBuilder<SpecialDungeonClearRewardItem>((at - start) / 3);
                            for (int row = start; row < at; row += 3)
                            {
                                int itemId = tokens[row].Value, probability = tokens[row + 1].Value, count = tokens[row + 2].Value;
                                if (itemId <= 0 || probability is <= 0 or > SpecialDungeonClearReward.ProbabilityScale || count <= 0)
                                    throw new InvalidDataException("特殊地下城通关奖励物品、概率或数量无效");
                                items.Add(new(itemId, probability, count));
                            }
                            clearRewardItems = items.MoveToImmutable();
                            continue;
                        }
                        if (childTag is "[special clear reward]" or "[special clear reward item list]")
                        {
                            string expectedClose = "[/" + childTag[1..];
                            int start = at + 1;
                            at = start;
                            while (at < tokens.Count && tokens[at].Type == 0) at++;
                            if (at == start || at >= tokens.Count || tokens[at].Type != 3 ||
                                resolve(tokens[at].Value).Trim().ToLowerInvariant() != expectedClose)
                                throw new InvalidDataException("特殊地下城通关奖励附加段未正确闭合: " + childTag);
                            continue;
                        }
                        throw new InvalidDataException("特殊地下城通关奖励含未知子段: " + childTag);
                    }
                    if (!closedIndex || clearRewardItems.IsDefaultOrEmpty)
                        throw new InvalidDataException("特殊地下城通关奖励缺少clear reward段或未闭合: " + dungeonId);
                    if (!result.TryAdd(dungeonId, new(dungeonType, mailSender, mailText, clearRewardItems)))
                        throw new InvalidDataException("特殊地下城通关奖励副本索引重复: " + dungeonId);
                }
                if (!closedType || result.Count == 0)
                    throw new InvalidDataException("特殊地下城通关奖励类型段未闭合或为空");
            }
        }
        if (inside) throw new InvalidDataException("特殊地下城通关奖励表未闭合");
        return result.ToImmutableDictionary();
    }
}
