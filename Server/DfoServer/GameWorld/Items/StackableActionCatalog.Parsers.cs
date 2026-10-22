using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal enum StackableActionParseStatus { Absent, InvalidDeclaration, UnknownLabel, InvalidArguments, Parsed }
internal enum StackableActionParserSource { Arad, PvfOnly }
internal enum StackableActionBusinessStatus { Pending, ExistingVariants }
internal sealed record StackableActionParser(StackableActionParserSource Source, StackableActionBusinessStatus Business,
    Func<StackableActionDeclaration, StackableTypeDeclaration?> Read);
internal sealed record StackableActionParseResult(StackableActionParseStatus Status, StackableActionIdentity? Identity,
    StackableTypeDeclaration? Arguments = null)
{
    public StackableActionDeclaration? Declaration { get; init; }
    public StackableActionParser? Parser { get; init; }
}

internal static partial class StackableActionCatalog
{
    // 延迟到外部映射初始化完成后装配，避免partial类静态字段的文件顺序依赖。
    static class ParserRegistry
    {
        internal static readonly FrozenDictionary<string, StackableActionParser> Entries = CreateParsers();
    }
    public static IReadOnlyDictionary<string, StackableActionParser> Parsers => ParserRegistry.Entries;
    public static IReadOnlyCollection<string> ParserLabels => ParserRegistry.Entries.Keys;

    static FrozenDictionary<string, StackableActionParser> CreateParsers()
    {
        var result = new Dictionary<string, StackableActionParser>(StringComparer.Ordinal);
        void Register(string label, StackableActionParserSource source)
        {
            var business = label switch
            {
                "[quest clear]" or "[any quest clear]" or "[first awaken clear]" or "[second awaken clear]" or
                "[level up ticket]" or "[jumping lv]" or "[class change]" or "[represent account name change]" or
                "[open aura skin slot]" or "[open creature skin slot]" or "[add skin storage]" or "[add fatigue]" or
                "[change creature]" or "[creature growth]" or "[kaleido]" or "[gold kaleido box]" or
                "[fixed probability kaleido]" or "[portable genuine damage upgrade upgrede]" or
                "[emancipate growth item upgrade]" or "[portable amplify]" or "[new portable amplify]" or
                "[lucky enchant deed]" or "[amplify protect equipment]" or "[new amplify protect equipment]"
                    or "[rent point]" or "[repair coupon]" or "[repair equipment]" or "[change creature random]" or "[creature effect]"
                    or "[creature use item]" or "[extend item period]" or "[creature transport stone]" or "[add mileage]"
                    => StackableActionBusinessStatus.ExistingVariants,
                _ => StackableActionBusinessStatus.Pending
            };
            if (!result.TryAdd(label, new(source, business, NumericSequence)))
                throw new InvalidOperationException("重复注册STK动作解析器: " + label);
        }
        // ARAD 04145D30..04145D93统一读数值列表；参数个数和用途属于具体消费模块。
        foreach (string label in ClientTypes.Keys) Register(label, StackableActionParserSource.Arad);
        // 当前PVF仍有声明，但本版040D4F60没有对应编号；不冒充相近的原生动作。
        foreach (string label in new[]
        {
            "[Amplify Upgrade Random]", "[Change RedMonkey Item]", "[add auctionpoint]", "[add damage font skin]",
            "[avatar inventory expansion]", "[change party frame]", "[change random avatar]", "[creature use item]",
            "[event extexsion day item]", "[force event resetdaily]", "[force init bingo board]", "[free repair equipment]",
            "[instant emoticon]", "[level up decision change]", "[miku music piece]", "[steampunk]", "[yet game item]"
        }) Register(label, StackableActionParserSource.PvfOnly);
        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    static StackableTypeDeclaration? NumericSequence(StackableActionDeclaration action)
    {
        if (action.HasIntegerArguments) return new(action.Label, action.Parameters);
        var values = ImmutableArray.CreateBuilder<int>(action.Arguments.Length);
        foreach (var argument in action.Arguments)
        {
            if (argument.Type is 0 or 1) values.Add(argument.Value);
            else if (argument.Type == 2)
            {
                // 3DB05E0接受type2并向零截断；不将原始float位模式当成整数值。
                float value = BitConverter.Int32BitsToSingle(argument.Value);
                if (!float.IsFinite(value) || (double)value < int.MinValue || (double)value > int.MaxValue) return null;
                values.Add((int)value);
            }
            else return null;
        }
        return new(action.Label, values.ToImmutable());
    }

    public static StackableActionParseResult Parse(StackableActionDeclaration? declaration)
    {
        if (declaration is null) return new(StackableActionParseStatus.Absent, null);
        bool known = TryType(declaration.Label, out uint type);
        var identity = new StackableActionIdentity(declaration.Label, known ? type : null);
        if (!Parsers.TryGetValue(declaration.Label, out var parser))
            return new(StackableActionParseStatus.UnknownLabel, identity)
                { Declaration = declaration };
        var arguments = parser.Read(declaration);
        return new(arguments is null ? StackableActionParseStatus.InvalidArguments : StackableActionParseStatus.Parsed, identity, arguments)
            { Declaration = declaration, Parser = parser };
    }

    public static StackableActionParseResult ParseFile(StackableFile file) => file.HasActionType && file.ActionDeclaration is null
        ? new(StackableActionParseStatus.InvalidDeclaration, null) : Parse(file.ActionDeclaration);

    // 既有消费模块只接过type0整数声明，不能因为扩展原生解析就同时放宽其业务资格。
    public static StackableTypeDeclaration? Arguments(StackableActionDeclaration? declaration) =>
        declaration is { HasIntegerArguments: true } ? Parse(declaration).Arguments : null;
    public static StackableTypeDeclaration? Arguments(StackableFile file) =>
        file.ActionDeclaration is { HasIntegerArguments: true } ? ParseFile(file).Arguments : null;
}
