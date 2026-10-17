using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.Network.Management;

// 从物品脚本文本解析“词条 / 期限 / 技能职业 / 头像属性 / 技能加成”：预览与物品库高级筛选索引共用，
// 只做文本模式识别（属性标签表沿用 GmItemStatSpecs），不改变发放使用的 ItemCatalog 解析。
internal static class GmItemAttributes
{
    internal sealed record Attrs(string[] Stats, byte Period, string[] SkillJobs, bool AllSkill);

    static readonly Regex Tagged = new(@"\[(?<type>[^\]/\r\n]+)\](?<values>(?:[ \t\r\n]+-?\d+)+)", RegexOptions.Compiled);
    static readonly Regex Ability = new(@"(?<idx>\d+)\s+`\[(?<tag>[A-Za-z_0-9 ]+)\]`\s+`(?<op>[^`]*)`\s+(?<value>-?\d+)", RegexOptions.Compiled);
    static readonly Regex Period = new(@"\[usable period\]\s*(\d+)", RegexOptions.Compiled);
    static readonly Regex Expiry = new(@"\[expiration date\]\s*`([^`]+)`", RegexOptions.Compiled);
    static readonly Regex JobTag = new(@"`\[([^\]]+)\]`", RegexOptions.Compiled);
    static readonly Regex LevelUp = new(@"`\[(?<job>[^\]]+)\]`\s+(?<skill>\d+)\s+(?<level>-?\d+)", RegexOptions.Compiled);
    static readonly Regex DataUp = new(@"`\[(?<job>[^\]]+)\]`\s+(?<skill>\d+)\s+`\[[^\]]+\]`\s+`\[(?<kind>[^\]]+)\]`(?:\s+(?<idx>\d+))?\s+`(?<op>[^`]*)`\s+(?<value>-?\d+)", RegexOptions.Compiled);

    internal static string Block(string text, string name)
    {
        var match = Regex.Match(text, @"\[" + Regex.Escape(name) + @"\](.*?)\[/" + Regex.Escape(name) + @"\]", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : "";
    }

    static IEnumerable<string> Blocks(string text, string name) =>
        Regex.Matches(text, @"\[" + Regex.Escape(name) + @"\](.*?)\[/" + Regex.Escape(name) + @"\]", RegexOptions.Singleline).Select(match => match.Groups[1].Value);

    // 脚本中出现的属性词条键（含头像属性块）；用于筛选，不关心数值。
    internal static string[] StatKeys(string text)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Tagged.Matches(text))
            if (GmItemStatSpecs.Table.TryGetValue(match.Groups["type"].Value.Replace('_', ' ').Trim(), out var spec)) keys.Add(spec.Key);
        foreach (string block in Blocks(text, "avatar select ability"))
            foreach (Match match in Ability.Matches(block))
                if (GmItemStatSpecs.Table.TryGetValue(match.Groups["tag"].Value.Replace('_', ' ').Trim(), out var spec)) keys.Add(spec.Key);
        return [.. keys.Order(StringComparer.Ordinal)];
    }

    internal static Attrs Parse(string text)
    {
        byte period = Expiry.IsMatch(text) ? (byte)2 : Period.Match(text) is { Success: true } days && int.TryParse(days.Groups[1].Value, out int value) && value > 0 ? (byte)1 : (byte)0;
        var jobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "skill levelup", "skill data up" })
            foreach (string block in Blocks(text, name))
                foreach (Match match in JobTag.Matches(block))
                    if (match.Groups[1].Value is { Length: > 0 } tag && Regex.IsMatch(tag, "^[a-z ]+$")) jobs.Add(tag);
        return new(StatKeys(text), period, [.. jobs], text.Contains("[all skill item]", StringComparison.Ordinal));
    }

    static string Label(string tag) => GmItemStatSpecs.Table.TryGetValue(tag.Replace('_', ' ').Trim(), out var spec) ? spec.Label : tag;

    // [avatar select ability]：时装可选属性，每项一条。
    internal static string[] AvatarAbility(string text) =>
        [.. Blocks(text, "avatar select ability").SelectMany(block => Ability.Matches(block).Select(match =>
            $"选项{match.Groups["idx"].Value}：{Label(match.Groups["tag"].Value)} {Signed(match.Groups["value"].Value)}{(match.Groups["op"].Value == "%" ? "%" : "")}"))];

    // 技能等级/数据加成；技能只显示编号（技能名属于另一份资源，不在这里解析）。
    internal static string[] SkillBonus(string text)
    {
        var lines = new List<string>();
        foreach (string block in Blocks(text, "skill levelup"))
            foreach (Match match in LevelUp.Matches(block))
                lines.Add($"{match.Groups["job"].Value} 技能#{match.Groups["skill"].Value} 等级 {Signed(match.Groups["level"].Value)}");
        foreach (string block in Blocks(text, "skill data up"))
            foreach (Match match in DataUp.Matches(block))
                lines.Add($"{match.Groups["job"].Value} 技能#{match.Groups["skill"].Value} {match.Groups["kind"].Value}{(match.Groups["idx"].Success ? "[" + match.Groups["idx"].Value + "]" : "")} {Signed(match.Groups["value"].Value)}{(match.Groups["op"].Value == "%" ? "%" : "")}");
        if (text.Contains("[all skill item]", StringComparison.Ordinal))
        {
            string all = Block(text, "all skill item");
            var bounds = Regex.Match(text, @"\[lower bound level\]\s*(\d+)\s*\[upper bound level\]\s*(\d+)\s*\[value\]\s*(-?\d+)");
            lines.Add(bounds.Success ? $"全职业技能 Lv{bounds.Groups[1].Value}–{bounds.Groups[2].Value} 等级 {Signed(bounds.Groups[3].Value)}" : "全技能等级加成（见脚本 [all skill item]）");
        }
        return [.. lines.Take(40), .. lines.Count > 40 ? new[] { $"…共 {lines.Count} 条" } : []];
    }

    // 套装效果脚本里的 [piece set ability] 段：件数 + 说明文本（parameter basic explain，缺失时退回属性行）。
    internal static (int Pieces, string[] Lines)[] SetTiers(string equText, Func<string, string[]> stats) =>
        [.. Regex.Matches(equText, @"\[piece set ability\]\s*(\d+)(.*?)\[/piece set ability\]", RegexOptions.Singleline).Select(match =>
        {
            string body = match.Groups[2].Value;
            string explain = Block(body, "parameter basic explain");
            var lines = explain.Length > 0
                ? Regex.Matches(explain, "`([^`]*)`").SelectMany(item => item.Groups[1].Value.Replace("%%", "%").Split('\n')).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray()
                : stats(body);
            return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), lines);
        })];

    static string Signed(string value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) && number >= 0 ? "+" + number.ToString(CultureInfo.InvariantCulture) : value;
}
