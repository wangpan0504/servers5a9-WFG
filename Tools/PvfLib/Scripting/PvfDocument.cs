using System.Text.RegularExpressions;

namespace PvfLib;

/// <summary>按行首标签索引的只读脚本视图；显式空字段和零值覆盖父脚本。</summary>
public sealed class PvfDocument
{
    private readonly string text;
    private readonly PvfDocument? parent;
    private readonly Dictionary<string, (int Start, int End, int Count)> fields;

    public PvfDocument(string text, PvfDocument? parent = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        this.text = text;
        this.parent = parent;
        fields = new(StringComparer.OrdinalIgnoreCase);
        int at = text.StartsWith('[') ? 0 : text.IndexOf("\n[", StringComparison.Ordinal) + 1;
        if (at == 0 && !text.StartsWith('['))
            return;
        while (at < text.Length)
        {
            int next = text.IndexOf("\n[", at, StringComparison.Ordinal);
            int end = next < 0 ? text.Length : next > 0 && text[next - 1] == '\r' ? next - 1 : next;
            int close = text.IndexOf(']', at + 1);
            if (close > at + 1 && close < end && text.AsSpan(at + 1, close - at - 1).IndexOfAny('\r', '\n') < 0)
            {
                string name = text[(at + 1)..close];
                if (fields.TryGetValue(name, out var first))
                    fields[name] = (first.Start, first.End, first.Count + 1);
                else
                    fields.Add(name, (close + 1, end, 1));
            }
            if (next < 0)
                break;
            at = next + 1;
        }
    }

    private PvfDocument(PvfDocument own, PvfDocument parent)
    {
        text = own.text;
        fields = own.fields;
        this.parent = parent;
    }

    internal PvfDocument WithParent(PvfDocument value) => new(this, value);
    internal int DeclarationCount(string tag) => fields.TryGetValue(tag, out var field) ? field.Count : 0;

    public bool Has(string tag) => fields.ContainsKey(tag) || parent?.Has(tag) == true;
    public bool TryGetInt32(string tag, out int value)
    {
        value = 0;
        if (!fields.TryGetValue(tag, out var field))
            return parent?.TryGetInt32(tag, out value) == true;
        // 最近声明层必须是唯一完整整数；显式空或重复不能借父模板补成有效值。
        return field.Count == 1 && int.TryParse(text.AsSpan(field.Start, field.End - field.Start).Trim(),
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public bool TryGetString(string tag, out string value)
    {
        value = "";
        if (!fields.TryGetValue(tag, out var field))
            return parent?.TryGetString(tag, out value) == true;
        var textValue = text.AsSpan(field.Start, field.End - field.Start).Trim();
        if (field.Count != 1 || textValue.Length < 2 || textValue[0] != '`' || textValue[1..].IndexOf('`') != textValue.Length - 2)
            return false;
        value = textValue[1..^1].ToString();
        return true;
    }

    /// <summary>读取最近声明层的根级整数；默认严格唯一，明确开启时只兼容完整同值重复。缺席继承父层，坏值不继承。</summary>
    public int? ReadRootInt32(string tag, bool allowIdenticalDuplicates = false)
    {
        // 仅候选字段需要结构扫描；普通模板不为这一严格读取重复解析全部脚本。
        if (text.IndexOf("[" + tag, StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("[/" + tag, StringComparison.OrdinalIgnoreCase) < 0)
            return parent?.ReadRootInt32(tag, allowIdenticalDuplicates);
        if (text.Count(value => value == '`') % 2 != 0)
            throw new InvalidDataException("PVF根整数声明的字符串未闭合");
        string masked = System.Text.RegularExpressions.Regex.Replace(text, "`[^`]*`", match => new string(' ', match.Length));
        const string pattern = @"\[(/?)([^\[\]\r\n]+)\]";
        var tokens = System.Text.RegularExpressions.Regex.Matches(masked, pattern);
        bool present = tokens.Any(token => string.Equals(token.Groups[2].Value, tag, StringComparison.OrdinalIgnoreCase));
        bool malformed = System.Text.RegularExpressions.Regex.IsMatch(masked,
            @"\[/?" + System.Text.RegularExpressions.Regex.Escape(tag) + @"(?:[ \t][^\]\r\n]*)?(?:\r?$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
        if (malformed) throw new InvalidDataException("PVF根整数声明不完整");
        if (!present)
        {
            if (masked.IndexOf("[" + tag + "]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                masked.IndexOf("[/" + tag + "]", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidDataException("PVF根整数声明的标签结构无效");
            return parent?.ReadRootInt32(tag, allowIdenticalDuplicates);
        }
        if (System.Text.RegularExpressions.Regex.Replace(masked, pattern, "").IndexOfAny(['[', ']']) >= 0)
            throw new InvalidDataException("PVF根整数声明包含残缺标签");
        var closed = tokens.Where(token => token.Groups[1].Length != 0)
            .Select(token => token.Groups[2].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        int? result = null;
        for (int index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            string name = token.Groups[2].Value;
            if (token.Groups[1].Length != 0)
            {
                if (stack.Count == 0 || !string.Equals(stack.Pop(), name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("PVF根整数声明的块闭合冲突");
            }
            else
            {
                if (string.Equals(name, tag, StringComparison.OrdinalIgnoreCase))
                {
                    int end = index + 1 < tokens.Count ? tokens[index + 1].Index : text.Length;
                    if (stack.Count != 0 || closed.Contains(name) ||
                        !int.TryParse(text.AsSpan(token.Index + token.Length, end - token.Index - token.Length).Trim(),
                            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value))
                        throw new InvalidDataException("PVF根整数声明必须唯一且完整");
                    if (result.HasValue && (!allowIdenticalDuplicates || result.Value != value))
                        throw new InvalidDataException("PVF根整数声明重复或冲突");
                    result = value;
                }
                if (closed.Contains(name)) stack.Push(name);
            }
        }
        if (stack.Count != 0 || !result.HasValue) throw new InvalidDataException("PVF根整数声明结构无效");
        return result;
    }

    // 分类可分多次声明；按完整字符串匹配，不能把 random clear avatar 认作 clear avatar。
    public bool ContainsString(string tag, string value)
    {
        if (!fields.ContainsKey(tag))
            return parent?.ContainsString(tag, value) == true;
        foreach (Match field in Regex.Matches(text, @"^\[" + Regex.Escape(tag) + @"\](.*?)(?=\r?\n\[|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase))
            foreach (Match token in Regex.Matches(field.Groups[1].Value, "`([^`]*)`"))
                if (string.Equals(token.Groups[1].Value, value, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    // 按源顺序保留全部标量声明，由消费者逐条校验并采用对应资源的覆盖规则。
    public IReadOnlyList<string> Sections(string tag)
    {
        if (!fields.TryGetValue(tag, out var field)) return parent?.Sections(tag) ?? [];
        if (field.Count == 1) return [text.AsSpan(field.Start, field.End - field.Start).TrimStart().ToString()];
        var declarations = Regex.Matches(text, @"^\[" + Regex.Escape(tag) + @"\](.*?)(?=\n\[|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (declarations.Count != field.Count) throw new InvalidDataException("PVF重复字段索引不完整: " + tag);
        return declarations.Select(declaration => declaration.Groups[1].Value.TrimStart()).ToArray();
    }

    public string Section(string tag) => fields.TryGetValue(tag, out var field)
        ? text.AsSpan(field.Start, field.End - field.Start).TrimStart().ToString()
        : parent?.Section(tag) ?? "";
    public string Block(string tag) => fields.ContainsKey(tag) ? PvfScript.BlockAllowingIdenticalDuplicates(text, tag) : parent?.Block(tag) ?? "";
}
