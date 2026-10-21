using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal readonly record struct BoosterUpgradeChance(byte Level, int Weight);
internal sealed record BoosterChoice(int ItemId, int Count, byte Upgrade = 0, byte Refinement = 0)
{
    public ImmutableArray<BoosterUpgradeChance> UpgradeChances { get; init; } = [];
    public ItemKind? RequiredKind { get; init; }
    public bool NativeCreatureSelectionRow { get; init; }
}
internal sealed record BoosterSelectionCategory(byte Category, byte Subcategory, ImmutableArray<BoosterChoice> Rewards,
    string? PendingRule = null)
{
    public ImmutableArray<int> DefaultItemIds { get; init; } = [];
    public ImmutableArray<int> RecommendedItemIds { get; init; } = [];
}
internal sealed record BoosterSelectionDefinition(int SelectionCount, ImmutableArray<BoosterSelectionCategory> Categories)
{
    internal static BoosterDefinition Parse(string text)
    {
        if (Regex.Matches(text, @"\[booster selection num\]").Count != 1 ||
            !int.TryParse(PvfScript.Section(text, "booster selection num").Trim(), out int count) || count < 0 ||
            text.Contains("[booster info]", StringComparison.Ordinal))
            return new([], "选择数量无效或包含额外随机奖池");
        var matches = Regex.Matches(text, @"\[booster select category\](.*?)\[/booster select category\]", RegexOptions.Singleline);
        if (matches.Count == 0 || matches.Count != Regex.Matches(text, @"\[booster select category\]").Count)
            return new([], "选择分类缺失或嵌套无效");
        var categories = ImmutableArray.CreateBuilder<BoosterSelectionCategory>();
        var firstByKey = new Dictionary<(byte, byte), (BoosterSelectionCategory Parsed, string Body)>();
        bool duplicated = false;
        foreach (Match match in matches)
        {
            string body = match.Groups[1].Value;
            var header = Regex.Match(body, @"^\s*(\d+)\s+(\d+)\s*");
            if (!header.Success || !byte.TryParse(header.Groups[1].Value, out byte category) ||
                !byte.TryParse(header.Groups[2].Value, out byte subcategory))
                return new([], "选择分类索引无效或重复");
            var parsed = ParseCategory(category, subcategory, body[header.Length..]);
            if (firstByKey.TryGetValue((category, subcategory), out var first))
            {
                duplicated = true;
                // 只兼容完整token等价的重复；保留原块，按原单份分类选择，不能拼接奖励或绕过语法校验。
                if (parsed.PendingRule is not null || first.Parsed.PendingRule is not null ||
                    !CategoryTokens(first.Body).SequenceEqual(CategoryTokens(body), StringComparer.Ordinal))
                    return new([], "选择分类索引无效或重复");
            }
            else
                firstByKey.Add((category, subcategory), (parsed, body));
            categories.Add(parsed);
        }
        if (duplicated && (count == 0 || Regex.Matches(text, @"\[/booster select category\]").Count != matches.Count ||
            categories.Any(category => category.PendingRule is not null) ||
            !HasDuplicateCategoryBounds(text, count, matches, categories)))
            return new([], "重复分类的类型、维数或坐标无效");
        return new([], Selection: new(count, categories.ToImmutable()));
    }

    static IEnumerable<string> CategoryTokens(string body) =>
        Regex.Matches(body, @"`[^`]*`|\[[^\]\r\n]*\]|[^\s\[\]`]+|[^\s]").Select(match => match.Value);

    static bool HasDuplicateCategoryBounds(string text, int count, MatchCollection blocks,
        IEnumerable<BoosterSelectionCategory> categories)
    {
        string masked = Regex.Replace(text, "`[^`]*`", match => new string(' ', match.Length));
        string? RootField(string name)
        {
            var fields = Regex.Matches(masked, @"\[" + Regex.Escape(name) + @"\]");
            if (fields.Count != 1 || !BoosterCatalog.IsRootDeclaration(masked, fields[0].Index))
                return null;
            int start = fields[0].Index + fields[0].Length;
            int end = masked.IndexOf('[', start);
            return text[start..(end < 0 ? text.Length : end)].Trim();
        }
        if (blocks.Any(block => !BoosterCatalog.IsRootDeclaration(masked, block.Index)))
            return false;
        string? type = RootField("stackable type");
        if (type is null || !Regex.IsMatch(type, @"^`\[booster selection\]`\s+0$"))
            return false;
        if (!int.TryParse(RootField("booster selection num"), out int rootCount) || rootCount != count)
            return false;
        string[]? dimensions = RootField("booster category num")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (dimensions is null || dimensions.Length is not (2 or 3) ||
            !int.TryParse(dimensions[0], out int rank) || rank != dimensions.Length - 1 ||
            !int.TryParse(dimensions[1], out int size) || size is < 1 or > 256)
            return false;
        int subsize = 1;
        if (rank == 2 && (!int.TryParse(dimensions[2], out subsize) || subsize is < 1 or > 256))
            return false;
        return categories.All(category => category.Category <= 127 && category.Subcategory <= 127 &&
            category.Category < size && category.Subcategory < subsize);
    }

    static BoosterSelectionCategory ParseCategory(byte category, byte subcategory, string body)
    {
        var rewards = ImmutableArray.CreateBuilder<BoosterChoice>();
        var defaults = ImmutableArray.CreateBuilder<int>();
        var recommended = ImmutableArray.CreateBuilder<int>();
        byte upgrade = 0, refinement = 0;
        var chances = ImmutableArray.CreateBuilder<BoosterUpgradeChance>();
        int at = 0;
        foreach (Match match in Regex.Matches(body, @"\[(?<kind>[^/\]\r\n]+)\](?<data>[^\[]*)"))
        {
            if (!string.IsNullOrWhiteSpace(body[at..match.Index]))
                return new(category, subcategory, [], "未识别的分类字段");
            at = match.Index + match.Length;
            string kind = match.Groups["kind"].Value;
            string[] tokens = match.Groups["data"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (kind is "default select" or "recommend")
            {
                if (tokens.Length == 0 || !int.TryParse(tokens[0], out int count) || count < 0 || count != tokens.Length - 1)
                    return new(category, subcategory, [], "选择提示清单行宽无效");
                var ids = kind == "default select" ? defaults : recommended;
                foreach (string token in tokens.Skip(1))
                {
                    if (!int.TryParse(token, out int id))
                        return new(category, subcategory, [], "选择提示编号无效");
                    ids.Add(id);
                }
                // 客户端只给实际候选设置预选/推荐标记；清单中的旧编号不生成奖励，也不改变物品行数量。
                continue;
            }
            if (kind is "booster equipment upgrade" or "booster equipment separate")
            {
                if (tokens.Length != 1 || !byte.TryParse(tokens[0], out byte value) ||
                    (kind == "booster equipment upgrade" ? value > 31 && value != 255 : value > 127))
                    return new(category, subcategory, [], "装备修饰数值超出字段范围");
                if (kind == "booster equipment upgrade")
                    upgrade = value;
                else
                    refinement = value;
                continue;
            }
            if (kind == "random probability")
            {
                const string end = "[/random probability]";
                if (upgrade != 255 || tokens.Length == 0 || tokens.Length % 2 != 0 ||
                    !body.AsSpan(at).StartsWith(end, StringComparison.Ordinal))
                    return new(category, subcategory, [], "随机强化概率段无效");
                at += end.Length;
                for (int i = 0; i < tokens.Length; i += 2)
                {
                    if (!byte.TryParse(tokens[i], out byte level) || level > 31 ||
                        !int.TryParse(tokens[i + 1], out int weight) || weight < 0)
                        return new(category, subcategory, [], "随机强化等级或权重无效");
                    chances.Add(new(level, weight));
                }
                continue;
            }
            if (kind is not ("equipment" or "stackable" or "etc" or "cera" or "creature"))
                return new(category, subcategory, [], kind);
            string closing = "[/" + kind + "]";
            if (!body.AsSpan(at).StartsWith(closing, StringComparison.Ordinal))
                return new(category, subcategory, [], "选择奖励段未闭合");
            at += closing.Length;
            if (tokens.Length % 2 != 0)
                return new(category, subcategory, [], "选择奖励行宽无效");
            for (int i = 0; i < tokens.Length; i += 2)
            {
                if (!int.TryParse(tokens[i], out int id) || id <= 0 || !int.TryParse(tokens[i + 1], out int amount) || amount <= 0)
                    return new(category, subcategory, [], "选择奖励物品或数量无效");
                if (kind == "equipment")
                {
                    if (upgrade == 255 && !chances.Any(row => row.Weight > 0))
                        return new(category, subcategory, [], "随机强化缺少有效概率");
                    // 修饰只作用于本分类后续的装备行；每行冻结当时的等级与概率，不追溯修改前面的产物。
                    rewards.Add(new(id, amount, upgrade, refinement)
                    {
                        UpgradeChances = upgrade == 255 ? chances.ToImmutable() : []
                    });
                }
                else
                    rewards.Add(new(id, amount) { NativeCreatureSelectionRow = kind == "creature" });
            }
        }
        return !string.IsNullOrWhiteSpace(body[at..]) ? new(category, subcategory, [], "未识别的分类尾段") :
            new(category, subcategory, rewards.ToImmutable())
            {
                DefaultItemIds = defaults.ToImmutable(), RecommendedItemIds = recommended.ToImmutable()
            };
    }
}
