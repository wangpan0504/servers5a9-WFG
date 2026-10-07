namespace PvfLib;

/// <summary>物品列表的资源身份与路径；不判断背包准入或装备槽位。</summary>
public static class PvfItemIndex
{
    public static Dictionary<int, string> Equipment(PvfToken[] tokens, Func<int, string> resolve, Func<string, bool> isTokenScript) =>
        Parse(tokens, resolve, isTokenScript, "equipment", "装备", 1);

    public static Dictionary<int, string> Stackable(PvfToken[] tokens, Func<int, string> resolve, Func<string, bool> isTokenScript) =>
        Parse(tokens, resolve, isTokenScript, "stackable", "堆叠物品", 0);

    private static Dictionary<int, string> Parse(PvfToken[] tokens, Func<int, string> resolve,
        Func<string, bool> isTokenScript, string root, string label, int minimumId)
    {
        if (tokens.Length == 0 || tokens.Length % 2 != 0)
            throw new InvalidDataException(label + "索引长度无效");
        var paths = new Dictionary<int, string>(tokens.Length / 2);
        for (int i = 0; i < tokens.Length; i += 2)
        {
            if (tokens[i].Type != 0 || tokens[i].Value < minimumId || tokens[i + 1].Type != 6)
                throw new InvalidDataException(label + "索引ID或路径token非法");
            string relative = resolve(tokens[i + 1].Value);
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || relative.StartsWith('\\') ||
                relative.Contains(':') || relative.Replace('\\', '/').Split('/').Any(part => part is "." or ".."))
                throw new InvalidDataException(label + "索引路径越界或非法");
            string path = PvfArchive.NormalizePath(root + "/" + relative);
            // 列表可含不同扩展名；由目录归属和 token 类型判断，不按扩展名分类。
            if (!path.StartsWith(root + "/", StringComparison.Ordinal) || !isTokenScript(path) || !paths.TryAdd(tokens[i].Value, path))
                throw new InvalidDataException(label + "索引重复ID、非脚本或资源缺失: " + tokens[i].Value + " / " + path);
        }
        return paths;
    }
}
