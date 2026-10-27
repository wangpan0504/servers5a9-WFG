using System.Collections.Immutable;

namespace PvfLib;

public readonly record struct ChronicleCategory(byte Job, byte GrowType, string Part);
public sealed record ChronicleEnchantFile(string? Import, byte? Type, (int First, int Second)? Probability,
    ImmutableArray<ChronicleCategory> CharacterChecks,
    ImmutableDictionary<ChronicleCategory, ImmutableHashSet<byte>> Skills)
{
    public ImmutableHashSet<string> DeclaredFields { get; init; } = [];

    public static ChronicleEnchantFile Parse(PvfToken[] tokens, Func<int, string> resolve, bool equipment = false)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        string? import = null;
        var references = Fields("import script");
        if (references.Length != 0)
        {
            Require(references is [{ Values.Length: 1, Children.IsEmpty: true }] && references[0].Values[0].Type == 6);
            import = resolve(references[0].Values[0].Value);
        }
        byte? type = null;
        var types = Fields("type");
        if (!equipment && types.Length != 0)
        {
            Require(types is [{ Values.Length: 1, Children.IsEmpty: true }] && types[0].Values[0] is { Type: 0, Value: >= 0 and <= 3 });
            type = (byte)types[0].Values[0].Value;
        }
        (int, int)? probability = null;
        var rates = Fields("probability");
        if (!equipment && rates.Length != 0)
        {
            Require(rates is [{ Values.Length: 2, Children.IsEmpty: true }] &&
                rates[0].Values.All(token => token is { Type: 0, Value: >= 0 and <= 100 }));
            probability = (rates[0].Values[0].Value, rates[0].Values[1].Value);
        }
        var checks = ImmutableArray.CreateBuilder<ChronicleCategory>();
        foreach (var field in Fields("character item check"))
        {
            Require(field.IsBlock && field.Children.IsEmpty && field.Values.Length is > 0 and <= 768 && field.Values.Length % 3 == 0);
            for (int at = 0; at < field.Values.Length; at += 3)
                checks.Add(Category(field.Values.AsSpan(at, 3)));
        }
        var skills = ImmutableDictionary.CreateBuilder<ChronicleCategory, ImmutableHashSet<byte>>();
        var tables = Fields("3choro enchant");
        if (!equipment && tables.Length != 0)
        {
            Require(tables is [{ IsBlock: true, Values.IsEmpty: true }]);
            foreach (var field in tables[0].Children)
            {
                Require(field.IsBlock && field.Name is "check" or "monitor");
                var categories = new List<ChronicleCategory>();
                if (field.Name == "check") categories.Add(Category(field.Values.AsSpan()));
                else
                {
                    // 本版monitor共用一组技能，第三项是后续部位名称的数量。
                    Require(field.Values.Length >= 4 && field.Values[2] is { Type: 0, Value: > 0 } &&
                        field.Values[2].Value == field.Values.Length - 3);
                    foreach (var part in field.Values.Skip(3))
                        categories.Add(Category(new[] { field.Values[0], field.Values[1], part }));
                }
                var numbers = ImmutableHashSet.CreateBuilder<byte>();
                foreach (var skill in field.Children.Where(row => row.Name == "skill"))
                {
                    // 效果由客户端按原表消费；服务端只核对实际声明的选项号，不重写技能数值。
                    Require(skill.IsBlock && (skill.Values.Length > 1 || !skill.Children.IsEmpty) &&
                        skill.Values[0] is { Type: 0, Value: >= 0 and <= byte.MaxValue });
                    // 多组技能效果可归同一选项；这里只建立可选编号索引，不改客户端效果合并顺序。
                    numbers.Add((byte)skill.Values[0].Value);
                }
                Require(numbers.Count > 0);
                var options = numbers.ToImmutable();
                foreach (var key in categories)
                {
                    if (skills.TryGetValue(key, out var previous))
                    {
                        // 原始键还包含选项编号；同职业/部位的后续check可以补充新的选项。
                        skills[key] = previous.Union(options);
                    }
                    else skills.Add(key, options);
                }
            }
        }
        return new(import, type, probability, checks.Distinct().ToImmutableArray(), skills.ToImmutable())
            { DeclaredFields = document.Sections.Select(field => field.Name).ToImmutableHashSet() };

        PvfTokenSection[] Fields(string name) => document.Sections.Where(field => field.Name == name).ToArray();
        ChronicleCategory Category(ReadOnlySpan<PvfToken> values)
        {
            Require(values.Length == 3 && values[0] is { Type: 0, Value: >= 0 and <= byte.MaxValue } &&
                values[1] is { Type: 0, Value: >= 0 and <= byte.MaxValue } && values[2].Type == 6);
            string part = resolve(values[2].Value).Trim('[', ']', ' ').ToLowerInvariant();
            Require(part.Length != 0);
            return new((byte)values[0].Value, (byte)values[1].Value, part);
        }
    }

    static void Require(bool valid)
    {
        if (!valid) throw new InvalidDataException("第三期异界装备气息声明无效或重复");
    }
}
