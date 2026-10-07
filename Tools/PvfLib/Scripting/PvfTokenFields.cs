namespace PvfLib.Scripting;

// 原始声明只记录 token 区间，不复制每个字段的数组，也不猜测重复声明的覆盖规则。
internal sealed class PvfTokenFields
{
    private readonly PvfToken[] tokens;
    private readonly Dictionary<string, (int Start, int Count)> fields = new(StringComparer.Ordinal);
    private readonly List<string> unsupported = [];

    internal PvfTokenFields(PvfToken[] tokens, Func<int, string> resolve, ReadOnlySpan<string> tags)
    {
        this.tokens = tokens;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Type != 3)
                continue;
            string tag = resolve(tokens[i].Value);
            if (tag.Length < 2 || tag[0] != '[' || tag[^1] != ']')
                continue;
            string name = tag[1..^1];
            if (!tags.Contains(name))
                continue;
            int start = ++i;
            while (i < tokens.Length && tokens[i].Type != 3)
                i++;
            if (!fields.TryAdd(name, (start, i - start)))
                fields[name] = (-1, 0);
            i--;
        }
    }

    internal bool TryGet(string name, out ReadOnlySpan<PvfToken> values)
    {
        if (fields.TryGetValue(name, out var field) && field.Start >= 0)
        {
            values = tokens.AsSpan(field.Start, field.Count);
            return true;
        }
        Unsupported(name);
        values = default;
        return false;
    }

    internal int? Scalar(string name, bool rejectInvalidValue = false)
    {
        if (!TryGet(name, out var values))
            return null;
        if (values.Length == 1)
        {
            if (values[0].Type == 0 && values[0].Value >= 0)
                return values[0].Value;
            if (rejectInvalidValue)
                throw new InvalidDataException("堆叠物品声明不是非负整数: " + name);
        }
        Unsupported(name);
        return null;
    }

    internal void Unsupported(string name) => unsupported.Add(name);
    internal IReadOnlyList<string> UnsupportedFields => Array.AsReadOnly(unsupported.Distinct(StringComparer.Ordinal).ToArray());
}
