using System.Globalization;
using System.Text.RegularExpressions;

namespace PvfLib;
// 通用脚本文本查询。玩法含义留在各 Catalog；不把跨版本字段含义塞进归档读取器。
public static partial class PvfScript
{
    public static string Section(string text, string tag) => Regex.Match(text, @"\[" + Regex.Escape(tag) + @"\]\s*(.*?)(?=\n\[|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value;
    public static string Block(string text, string tag)
    {
        var matches = Regex.Matches(text, @"\[" + Regex.Escape(tag) + @"\](.*?)\[/" + Regex.Escape(tag) + @"\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (matches.Count > 1)
            throw new InvalidDataException("重复 PVF 块: " + tag);
        return matches.Count == 0 ? "" : matches[0].Groups[1].Value;
    }

    // 物品脚本专用：新版 PVF 部分物品把同一块原样写了两遍。内容（忽略空白）逐字相同时无歧义，按一块读取；内容不同仍按重复拒绝。
    // impossible contents 是禁止用途的并集声明，同一物品可分多块各写一部分（如 ban redeemitem 与 gift 各一块），多块按并集读取。
    public static string BlockAllowingIdenticalDuplicates(string text, string tag)
    {
        var matches = Regex.Matches(text, @"\[" + Regex.Escape(tag) + @"\](.*?)\[/" + Regex.Escape(tag) + @"\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (matches.Count <= 1) return matches.Count == 0 ? "" : matches[0].Groups[1].Value;
        if (string.Equals(tag, "impossible contents", StringComparison.OrdinalIgnoreCase))
            return string.Join("\n", matches.Select(match => match.Groups[1].Value));
        string first = Regex.Replace(matches[0].Groups[1].Value, @"\s+", " ").Trim();
        if (matches.Skip(1).Any(match => !string.Equals(Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim(), first, StringComparison.Ordinal)))
            throw new InvalidDataException("重复 PVF 块: " + tag);
        return matches[0].Groups[1].Value;
    }

    public static int[] Integers(string text)
    {
        var values = new List<int>();
        foreach (var match in IntegerPattern().EnumerateMatches(text))
            values.Add(int.Parse(text.AsSpan(match.Index, match.Length), CultureInfo.InvariantCulture));
        return values.ToArray();
    }
    public static float Number(string text, string tag, float fallback = 0)
    {
        var match = NumberPattern().Match(Section(text, tag));
        return match.Success ? float.Parse(match.Value, CultureInfo.InvariantCulture) : fallback;
    }

    public static IReadOnlyDictionary<int, string> List(string text)
    {
        var rows = new Dictionary<int, string>();
        foreach (Match match in ListPattern().Matches(text))
        {
            int id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            string path = PvfArchive.NormalizePath(match.Groups[2].Value);
            if (!rows.TryAdd(id, path))
                throw new InvalidDataException("重复 PVF 列表 ID: " + id);
        }

        return rows.AsReadOnly();
    }

    [GeneratedRegex(@"-?\d+")]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"[-+]?\d+(?:\.\d+)?")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"(\d+)\s+`([^`]+)`")]
    private static partial Regex ListPattern();
}
