namespace PvfLib.IO;

internal static class PvfPath
{
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
