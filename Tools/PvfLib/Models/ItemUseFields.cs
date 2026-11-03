namespace PvfLib;

// 使用资格只读取原始声明，业务含义由资源目录解释。
public static class ItemUseFields
{
    public static bool IsSharedTag(string name) => name is "action usable place" or "action usable condition" or "usable world" ||
        IsIgnoredServerType(name);

    // 这三类声明仅保留原始元数据，不校验服务器范围、重复值或空值。
    public static bool IsIgnoredServerType(string name) =>
        name is "action usable server type" or "usable game server type" or "usable server type";

    public static IReadOnlyList<string>? ActionConditions(PvfToken[] tokens, Func<int, string> resolve) =>
        Strings(tokens, resolve, "[action usable condition]", append: true);

    public static IReadOnlyList<string>? UsableWorlds(PvfToken[] tokens, Func<int, string> resolve) =>
        Strings(tokens, resolve, "[usable world]", append: false);

    public static IReadOnlyList<string>? UsableWorlds(PvfDocument document)
    {
        var sections = document.Sections("usable world");
        if (sections.Count == 0) return null;
        return Array.AsReadOnly(System.Text.RegularExpressions.Regex.Matches(sections[^1], @"\G\s*`([^`]*)`")
            .Select(match => match.Groups[1].Value).ToArray());
    }

    static IReadOnlyList<string>? Strings(PvfToken[] tokens, Func<int, string> resolve, string tag, bool append)
    {
        List<string>? result = null;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Type != 3 || resolve(tokens[i].Value) != tag) continue;
            if (!append || result is null) result = [];
            while (i + 1 < tokens.Length && tokens[i + 1].Type == 6)
                result.Add(resolve(tokens[++i].Value));
        }
        return result is null ? null : Array.AsReadOnly(result.ToArray());
    }
}
