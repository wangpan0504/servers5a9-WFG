namespace PvfLib.IO;

internal static class PvfPath
{
    // 仅识别已规范化的归档目录/叶名；不修正输入，复杂路径仍交给原完整规则。
    internal static bool IsCanonicalDirectory(string path)
    {
        if (path.Length == 0) return true;
        if (path[0] == '/' || path[^1] == '/' || path.Contains('\\') ||
            path.AsSpan().Trim().Length != path.Length || !string.Equals(path, path.ToLowerInvariant(), StringComparison.Ordinal))
            return false;
        for (int start = 0; start < path.Length;)
        {
            int slash = path.IndexOf('/', start);
            int end = slash < 0 ? path.Length : slash;
            var part = path.AsSpan(start, end - start);
            if (part.IsEmpty || part.SequenceEqual(".") || part.SequenceEqual("..")) return false;
            start = end + 1;
        }
        return true;
    }

    internal static bool IsCanonicalLeaf(string name) => name.Length != 0 && name is not ("." or "..") &&
        name.AsSpan().IndexOfAny('/', '\\') < 0 && name.AsSpan().Trim().Length == name.Length &&
        string.Equals(name, name.ToLowerInvariant(), StringComparison.Ordinal);

    internal static string Normalize(string path)
    {
        string normalized = path.Trim().Replace('\\', '/').ToLowerInvariant().Trim('/');
        // 普通归档路径不需要 Split/List/Join；百万条目初始化时避免这些短期副本。
        bool simple = true;
        for (int start = 0; start < normalized.Length;)
        {
            int slash = normalized.IndexOf('/', start);
            int end = slash < 0 ? normalized.Length : slash;
            var part = normalized.AsSpan(start, end - start);
            if (part.IsEmpty || part.SequenceEqual(".") || part.SequenceEqual(".."))
            {
                simple = false;
                break;
            }
            start = end + 1;
        }
        if (simple)
            return normalized;
        var parts = new List<string>();
        foreach (string part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (part == "..")
            {
                if (parts.Count == 0)
                    throw new InvalidDataException("PVF 路径越过归档根");
                parts.RemoveAt(parts.Count - 1);
            }
            else if (part != ".")
                parts.Add(part);
        return string.Join('/', parts);
    }
}
