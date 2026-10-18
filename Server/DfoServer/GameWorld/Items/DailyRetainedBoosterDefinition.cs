using System.Text.RegularExpressions;

namespace DfoServer.GameWorld.Items;

// 此规则是本服每日保留政策；未知顶层字段不能被默认为无额外计数语义。
internal static class DailyRetainedBoosterDefinition
{
    static readonly HashSet<string> RootFields = new(StringComparer.Ordinal)
    {
        "name", "explain", "flavor text", "grade", "rarity", "usable job", "attach type",
        "minimum level", "maximum level", "required skill", "cool time", "icon", "stackable type",
        "move wav", "usable period", "stack limit", "booster info", "total usable count",
        "impossible contents", "hide booster info"
    };
    static readonly HashSet<string> Blocks = new(StringComparer.Ordinal) { "usable job", "booster info", "impossible contents" };

    public static bool TryParse(string text, out byte limit)
    {
        limit = 0;
        if (text.Count(value => value == '`') % 2 != 0) return false;
        string masked = Regex.Replace(text, "`[^`]*`", match => new string(' ', match.Length));
        const string tokenPattern = @"\[(/?)([^\]\r\n]+)\]";
        if (Regex.Replace(masked, tokenPattern, "").IndexOfAny(['[', ']']) >= 0) return false;
        var fields = new Dictionary<string, (int Start, int End)>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        string? current = null;
        int start = 0;
        foreach (Match token in Regex.Matches(masked, tokenPattern))
        {
            string key = token.Groups[2].Value;
            bool closing = token.Groups[1].Length != 0;
            if (stack.Count != 0)
            {
                if (closing)
                {
                    if (stack.Pop() != key) return false;
                    if (stack.Count == 0)
                    {
                        fields[current!] = (start, token.Index);
                        current = null;
                    }
                }
                else
                {
                    // 奖池子块仍由原有奖池解释器检验；其余块不接受嵌套字段。
                    if (stack.Last() != "booster info" || key == "booster info") return false;
                    stack.Push(key);
                }
                continue;
            }
            if (closing || !RootFields.Contains(key) || fields.ContainsKey(key)) return false;
            if (current is not null) fields[current] = (start, token.Index);
            current = key;
            start = token.Index + token.Length;
            fields.Add(key, (start, text.Length));
            if (Blocks.Contains(key)) stack.Push(key);
        }
        if (stack.Count != 0) return false;
        if (current is not null) fields[current] = (start, text.Length);
        if (!fields.TryGetValue("total usable count", out var count) ||
            !Regex.IsMatch(text[count.Start..count.End].Trim(), @"^[0-9]+$") ||
            !byte.TryParse(text[count.Start..count.End].Trim(), out limit) || limit == 0 ||
            !fields.TryGetValue("impossible contents", out var impossible)) return false;
        return Regex.IsMatch(text[impossible.Start..impossible.End], @"^\s*`delete booster`\s*$");
    }
}
