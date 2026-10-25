using System.Collections.Immutable;

namespace PvfLib;

public sealed record ItemRecipeEntry(int ItemId, int Count);
public sealed record ItemRecipeFile(int TemplateId, string ScriptPath, int SourceType,
    ImmutableArray<ItemRecipeEntry> Inputs, ImmutableArray<ItemRecipeEntry> Outputs,
    string? MaintainAbility, bool MaintainSocketStat, int SuccessPercent, string? MakingType, int MakingLevel,
    ImmutableArray<ItemRecipeEntry> Skills, ImmutableArray<string> Diagnostics)
{
    public ImmutableArray<ItemRecipeEntry> BeadInputs { get; init; } = [];

    public static ItemRecipeFile Parse(int id, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var errors = document.Diagnostics.ToBuilder();
        PvfTokenSection? Field(string name)
        {
            var fields = document.Sections.Where(section => section.Name == name).ToArray();
            if (fields.Length == 0) return null;
            if (fields is [{ Children.Length: 0 } field]) return field;
            errors.Add("配方声明重复或嵌套：" + name);
            return null;
        }
        ImmutableArray<ItemRecipeEntry> Pairs(string name, bool allowZero)
        {
            if (Field(name) is not { } field) return [];
            var values = field.Values;
            if (values.Length % 2 != 0 || values.Any(value => value.Type != 0))
            { errors.Add("配方二元组不完整：" + name); return []; }
            var result = ImmutableArray.CreateBuilder<ItemRecipeEntry>();
            for (int at = 0; at < values.Length; at += 2)
            {
                int item = values[at].Value, count = values[at + 1].Value;
                if (item < (allowZero ? 0 : 1) || count <= 0) errors.Add("配方编号或数量无效：" + name);
                result.Add(new(item, count));
            }
            return result.ToImmutable();
        }
        int sourceType = -1;
        if (Field("stackable type") is { Values: [{ Type: 6 } label, { Type: 0 } mode] } && resolve(label.Value) == "[recipe]")
            sourceType = mode.Value;
        else errors.Add("非完整[recipe]声明");
        string? maintain = null;
        if (Field("maintain ability") is { } ability)
        {
            if (ability.Values is [{ Type: 6 } value]) maintain = resolve(value.Value);
            else errors.Add("配方属性保留声明无效");
        }
        bool sockets = false;
        if (Field("maintain socket stat") is { } socket)
        {
            sockets = socket.Values.IsEmpty;
            if (!sockets) errors.Add("配方孔位保留声明无效");
        }
        int chance = 0;
        if (Field("success rate") is { } rate)
        {
            if (rate.Values is [{ Type: 0 } value]) chance = value.Value;
            else errors.Add("配方成功率声明无效");
        }
        string? makingType = null;
        int makingLevel = 0;
        if (Field("minimum itemmaking level") is { } making)
        {
            if (making.Values is [{ Type: 6 } type, { Type: 0 } level])
            { makingType = resolve(type.Value); makingLevel = level.Value; }
            else errors.Add("制作熟练度声明无效");
        }
        var inputs = Pairs("input item", true);
        var outputs = Pairs("output item", false);
        var skills = Pairs("need skill", true);
        var beadInputs = Pairs("bead item", false);
        return new(id, path, sourceType, inputs, outputs, maintain, sockets, chance, makingType, makingLevel, skills, errors.ToImmutable()) { BeadInputs = beadInputs };
    }
}
