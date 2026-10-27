using System.Collections.Immutable;

namespace PvfLib;

// 装备自己的转换声明；与设计图内的 emancipate group 三元组不是同一种格式。
public sealed record EquipmentEmancipationFile(int Type, ImmutableArray<EmancipateEntry> Inputs,
    ImmutableArray<EmancipateEntry> Outputs)
{
    public int GrowGroup { get; init; }
    public int GrowStage { get; init; }
    public static EquipmentEmancipationFile? Parse(PvfToken[] tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var blocks = document.Sections.Where(section => section.Name == "emancipate").ToArray();
        if (!document.Diagnostics.IsDefaultOrEmpty || blocks.Length != 1)
            return null;
        var block = blocks[0];
        var types = block.Children.Where(section => section.Name == "emancipate type").ToArray();
        if (!block.IsBlock || !block.Values.IsEmpty || block.Children.Any(section => section.Name is not ("input" or "output" or "emancipate type" or "emancipate explain" or "equipment grow")) ||
            types.Length > 1 || types.Length == 1 && (types[0].IsBlock || !types[0].Children.IsEmpty ||
            types[0].Values.Length != 1 || types[0].Values[0].Type != 0 || types[0].Values[0].Value < 0))
            return null;
        var explanations = block.Children.Where(section => section.Name == "emancipate explain").ToArray();
        if (explanations.Length > 1 || explanations.Length == 1 &&
            (explanations[0].IsBlock || explanations[0].Values is not [{ Type: 6 }])) return null;
        var growth = block.Children.Where(section => section.Name == "equipment grow").ToArray();
        if (growth.Length > 1 || growth.Length == 1 &&
            (growth[0].IsBlock || growth[0].Values is not [{ Type: 0, Value: > 0 }, { Type: 0, Value: > 0 }])) return null;
        var inputs = Pairs("input");
        var outputs = Pairs("output");
        return inputs is null || outputs is null ? null : new(types.Length == 0 ? 0 : types[0].Values[0].Value, inputs.Value, outputs.Value)
        { GrowGroup = growth.Length == 0 ? 0 : growth[0].Values[0].Value,
            GrowStage = growth.Length == 0 ? 0 : growth[0].Values[1].Value };

        ImmutableArray<EmancipateEntry>? Pairs(string name)
        {
            var sections = block.Children.Where(section => section.Name == name).ToArray();
            if (sections.Length != 1 || !sections[0].IsBlock || !sections[0].Children.IsEmpty ||
                sections[0].Values.Length == 0 || sections[0].Values.Length % 2 != 0 ||
                sections[0].Values.Any(token => token.Type != 0 || token.Value <= 0))
                return null;
            var result = ImmutableArray.CreateBuilder<EmancipateEntry>();
            var values = sections[0].Values;
            for (int i = 0; i < values.Length; i += 2)
                result.Add(new(values[i].Value, values[i + 1].Value, 0));
            return result.ToImmutable();
        }
    }
}
