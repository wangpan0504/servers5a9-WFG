using System.Collections.Immutable;

namespace PvfLib;

// 仅描述设计图学习声明；input/output 属于制作成本，不是学习时的额外消耗。
public sealed record ExpertRecipeFile(int TemplateId, bool Supported, string? RecipeKind,
    ImmutableArray<ItemRecipeEntry> Skills)
{
    public static ExpertRecipeFile Parse(int id, PvfToken[] tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        PvfTokenSection? Field(string name)
        {
            var found = document.Sections.Where(section => section.Name == name).ToArray();
            return found is [{ Children.Length: 0 } field] ? field : null;
        }
        var type = Field("stackable type");
        var strings = Field("string data");
        string? kind = strings?.Values is [{ Type: 6 } text] ? resolve(text.Value) : null;
        bool supported = document.Diagnostics.IsEmpty && strings?.IsBlock == true &&
            Field("int data")?.IsBlock == true && Field("need skill")?.IsBlock == true &&
            type?.Values is [{ Type: 6 } label, { Type: 0, Value: 0 }] && resolve(label.Value) == "[recipe]" &&
            Field("int data")?.Values is [{ Type: 0, Value: 2 }] &&
            (kind == "[enchant]" && Field("sub type")?.Values is [{ Type: 0, Value: 1 }] ||
             kind == "[craftmanship]" && Field("sub type")?.Values is [{ Type: 0, Value: 0 }]);
        var skills = ImmutableArray.CreateBuilder<ItemRecipeEntry>();
        if (Field("need skill") is { } required && required.Values.Length > 0 && required.Values.Length % 2 == 0)
        {
            for (int i = 0; i < required.Values.Length; i += 2)
            {
                var skill = required.Values[i];
                var level = required.Values[i + 1];
                if (skill.Type != 0 || level.Type != 0 || skill.Value is < 1 or > ushort.MaxValue || level.Value is < 1 or > byte.MaxValue)
                    supported = false;
                skills.Add(new(skill.Value, level.Value));
            }
        }
        else supported = false;
        return new(id, supported, kind, skills.ToImmutable());
    }
}
