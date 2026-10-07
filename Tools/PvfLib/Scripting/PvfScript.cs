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
