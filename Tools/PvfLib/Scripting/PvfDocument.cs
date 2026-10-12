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

    public string Section(string tag) => fields.TryGetValue(tag, out var field)
        ? text.AsSpan(field.Start, field.End - field.Start).TrimStart().ToString()
        : parent?.Section(tag) ?? "";
    public string Block(string tag) => fields.ContainsKey(tag) ? PvfScript.Block(text, tag) : parent?.Block(tag) ?? "";
}
