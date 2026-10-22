using System.Collections.Immutable;
using System.Globalization;

namespace PvfLib;

public readonly record struct StackableActionArgument(byte Type, int Value, string? Text = null);

// 只还原声明和参数类型；动作编号、参数含义与消费方式由玩法资源层注册。
public sealed record StackableActionDeclaration
{
    public string Label { get; }
    public ImmutableArray<StackableActionArgument> Arguments { get; }
    public bool HasIntegerArguments { get; }
    public IReadOnlyList<int> Parameters { get; }

    public StackableActionDeclaration(string label, IReadOnlyList<int> parameters)
        : this(label, parameters.Select(value => new StackableActionArgument(0, value)).ToImmutableArray()) { }

    StackableActionDeclaration(string label, ImmutableArray<StackableActionArgument> arguments)
    {
        Label = label;
        Arguments = arguments;
        HasIntegerArguments = arguments.All(value => value.Type == 0);
        Parameters = HasIntegerArguments ? arguments.Select(value => value.Value).ToImmutableArray() : [];
    }

    internal static StackableActionDeclaration? ReadValues(ReadOnlySpan<PvfToken> values, Func<int, string> resolve)
    {
        if (values.IsEmpty || values[0].Type != 6) return null;
        var arguments = ImmutableArray.CreateBuilder<StackableActionArgument>(values.Length - 1);
        foreach (var value in values[1..])
            arguments.Add(value.Type == 6 ? new(6, 0, resolve(value.Value)) : new(value.Type, value.Value));
        return new(resolve(values[0].Value), arguments.ToImmutable());
    }

    public static StackableActionDeclaration? Read(PvfTokenDocument document, Func<int, string> resolve)
    {
        var fields = document.Sections.Where(value => value.Name == "action type").ToArray();
        if (fields.Length == 0) return null;
        if (fields is not [{ Children.Length: 0 } field] || ReadValues(field.Values.AsSpan(), resolve) is not { } action)
            throw new InvalidDataException("动作声明必须唯一且以字符串标签开头");
        return action;
    }

    public static StackableActionDeclaration? Read(PvfDocument document)
    {
        if (!document.Has("action type")) return null;
        if (document.DeclarationCount("action type") > 1)
            throw new InvalidDataException("重复动作声明");
        var text = document.Section("action type").AsSpan();
        var values = ImmutableArray.CreateBuilder<StackableActionArgument>();
        int at = 0;
        while (at < text.Length)
        {
            if (char.IsWhiteSpace(text[at])) { at++; continue; }
            if (text[at..].StartsWith("//", StringComparison.Ordinal))
            {
                int line = text[at..].IndexOf('\n');
                if (line < 0) break;
                at += line + 1;
                continue;
            }
            if (text[at] == '`')
            {
                int end = text[(at + 1)..].IndexOf('`');
                if (end < 0) throw new InvalidDataException("动作字符串未闭合");
                values.Add(new(6, 0, text.Slice(at + 1, end).ToString()));
                at += end + 2;
                if (at < text.Length && !char.IsWhiteSpace(text[at]))
                    throw new InvalidDataException("动作参数缺少分隔符");
                continue;
            }
            int start = at;
            while (at < text.Length && !char.IsWhiteSpace(text[at])) at++;
            var number = text[start..at];
            if (int.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int integer))
                values.Add(new(0, integer));
            else if (number.IndexOfAny('.', 'e', 'E') >= 0 &&
                float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out float floating) && float.IsFinite(floating))
                values.Add(new(2, BitConverter.SingleToInt32Bits(floating)));
            else
                throw new InvalidDataException("动作参数不是完整整数、浮点数或引号字符串");
        }
        if (values.Count == 0 || values[0] is not { Type: 6, Text: { } label })
            throw new InvalidDataException("动作声明缺少字符串标签");
        return new(label, values.Skip(1).ToImmutableArray());
    }

    public static bool TryRead(PvfDocument document, out StackableActionDeclaration? declaration)
    {
        try { declaration = Read(document); return declaration is not null; }
        catch (InvalidDataException) { declaration = null; return false; }
    }
}
