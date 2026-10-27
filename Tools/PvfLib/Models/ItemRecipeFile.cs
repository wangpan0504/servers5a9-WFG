using System.Collections.Immutable;

namespace PvfLib;

public sealed record ItemRecipeEntry(int ItemId, int Count);
public sealed record ItemRecipeFile(int TemplateId, string ScriptPath, int SourceType,
    ImmutableArray<ItemRecipeEntry> Inputs, ImmutableArray<ItemRecipeEntry> Outputs,
    string? MaintainAbility, bool MaintainSocketStat, int SuccessRate, string? MakingType, int MakingLevel,
    ImmutableArray<ItemRecipeEntry> Skills, ImmutableArray<string> Diagnostics)
{
    public ImmutableArray<ItemRecipeEntry> BeadInputs { get; init; } = [];
    public ImmutableArray<ItemRecipeEntry> FailureOutputs { get; init; } = [];
    public string? CustomUpgradeType { get; init; }

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
                if (item < (allowZero ? 0 : 1) || count < 0 || count == 0 && name != "input item") errors.Add("配方编号或数量无效：" + name);
                if (count == 0 && name == "input item") continue;
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
        if (inputs.IsEmpty && outputs.IsEmpty && Field("int data") is { } packed && Field("input item") is null && Field("output item") is null)
        {
            // 旧图使用计数前缀的输入、输出二元组，尾部0表示没有额外条件。
            // 不截断未知后缀，否则附魔技能等前置会被绕过。
            int at = 0;
            ImmutableArray<ItemRecipeEntry> PackedPairs(bool allowGold)
            {
                if (at >= packed.Values.Length || packed.Values[at].Type != 0 || packed.Values[at].Value is < 1 or > 255)
                { errors.Add("旧配方计数无效"); return []; }
                int count = packed.Values[at++].Value;
                if (count > (packed.Values.Length - at) / 2) { errors.Add("旧配方二元组截断"); return []; }
                var entries = ImmutableArray.CreateBuilder<ItemRecipeEntry>();
                for (int i = 0; i < count; i++)
                {
                    var item = packed.Values[at++]; var amount = packed.Values[at++];
                    if (item.Type != 0 || amount.Type != 0 || item.Value < (allowGold ? 0 : 1) || amount.Value <= 0)
                        errors.Add("旧配方编号或数量无效");
                    entries.Add(new(item.Value, amount.Value));
                }
                return entries.ToImmutable();
            }
            inputs = PackedPairs(true); outputs = PackedPairs(false);
            if (packed.Values.Length - at != 1 || packed.Values[at].Type != 0 || packed.Values[at].Value != 0)
                errors.Add("旧配方含未识别的额外条件");
        }
        var skills = Pairs("need skill", true);
        var beadInputs = Pairs("bead item", false);
        var failures = Pairs("fail output", false);
        string? custom = null;
        if (Field("custom upgrade type") is { } table)
        {
            if (table.Values is [{ Type: 6 } name]) custom = resolve(name.Value);
            else errors.Add("配方强化表声明无效");
        }
        return new(id, path, sourceType, inputs, outputs, maintain, sockets, chance, makingType, makingLevel, skills, errors.ToImmutable())
        { BeadInputs = beadInputs, FailureOutputs = failures, CustomUpgradeType = custom };
    }
}
