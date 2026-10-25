using System.Collections.Immutable;

namespace PvfLib;

// 保留两种资源的原始拼写；首转与技能编号不在库层转换成职业规则或协议字段。
public sealed record TalismanFile(EquipmentFile Equipment, ImmutableArray<int>? DeclaredGrowTypes,
    int? DeclaredSkillIndex)
{
    public static TalismanFile Parse(int templateId, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        var document = TalismanItemFields.Read(tokens, resolve);
        _ = TalismanItemFields.Section(document, "equipment type");
        _ = TalismanItemFields.Section(document, "usable job");
        var growTypes = TalismanItemFields.Integers(document, "useable growtype");
        int? skill = TalismanItemFields.Integer(document, "rune skillindex");
        return new(EquipmentFile.Parse(templateId, path, tokens, resolve), growTypes, skill);
    }
}

public sealed record RuneOptionDeclaration(string Camp, int Grade);

public sealed record RuneFile(StackableFile Stackable, int? DeclaredSkillIndex, RuneOptionDeclaration? Option)
{
    public static RuneFile Parse(int templateId, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        var document = TalismanItemFields.Read(tokens, resolve);
        _ = TalismanItemFields.Section(document, "stackable type");
        _ = TalismanItemFields.Section(document, "usable job");
        int? skill = TalismanItemFields.Integer(document, "rune skillindex");
        var section = TalismanItemFields.Section(document, "rune option");
        RuneOptionDeclaration? option = null;
        if (section is not null)
        {
            if (section.Children.Length != 0 || section.Values is not [{ Type: 6 } camp, { Type: 0 } grade] ||
                string.IsNullOrWhiteSpace(resolve(camp.Value)))
                throw new InvalidDataException("符文rune option必须为阵营字符串及品级整数");
            option = new(resolve(camp.Value), grade.Value);
        }
        // 首转声明由既有原始STK模型保留；这里同样检查根字段，防止嵌套或重复声明被接受。
        _ = TalismanItemFields.Integers(document, "usable first growtype");
        return new(StackableFile.Parse(templateId, path, tokens, resolve), skill, option);
    }
}

internal static class TalismanItemFields
{
    internal static PvfTokenDocument Read(PvfToken[] tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve, new Dictionary<string, string>
        {
            ["usable job"] = "usable job", ["useable growtype"] = "useable growtype",
            ["usable first growtype"] = "usable first growtype"
        });
        if (!document.Diagnostics.IsEmpty)
            throw new InvalidDataException("护石或符文声明结构无效: " + document.Diagnostics[0]);
        return document;
    }

    internal static PvfTokenSection? Section(PvfTokenDocument document, string name)
    {
        var sections = document.Sections.Where(section => section.Name == name).ToArray();
        if (sections.Length > 1 || document.Sections.Any(section => ContainsNested(section, name)))
            throw new InvalidDataException("护石或符文字段重复或嵌套: " + name);
        return sections.SingleOrDefault();
    }

    internal static bool ContainsNested(PvfTokenSection section, string name) =>
        section.Children.Any(child => child.Name == name || ContainsNested(child, name));

    internal static ImmutableArray<int>? Integers(PvfTokenDocument document, string name)
    {
        var section = Section(document, name);
        if (section is null)
            return null;
        if (section.Values.IsEmpty || !section.Children.IsEmpty || section.Values.Any(token => token.Type != 0))
            throw new InvalidDataException("护石或符文字段必须为非空整数序列: " + name);
        return section.Values.Select(token => token.Value).ToImmutableArray();
    }

    internal static int? Integer(PvfTokenDocument document, string name)
    {
        var values = Integers(document, name);
        if (values is { Length: not 1 })
            throw new InvalidDataException("护石或符文字段必须为单个整数: " + name);
        return values is { } declared ? declared[0] : null;
    }
}
