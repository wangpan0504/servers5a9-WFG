using System.Collections.Immutable;

namespace PvfLib;

// 装备自己的转换声明；与设计图内的 emancipate group 三元组不是同一种格式。
public sealed record EquipmentEmancipationFile(int Type, ImmutableArray<EmancipateEntry> Inputs,
    ImmutableArray<EmancipateEntry> Outputs)
{
    public static EquipmentEmancipationFile? Parse(PvfToken[] tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var blocks = document.Sections.Where(section => section.Name == "emancipate").ToArray();
        if (!document.Diagnostics.IsDefaultOrEmpty || blocks.Length != 1)
            return null;
        var block = blocks[0];
        var types = block.Children.Where(section => section.Name == "emancipate type").ToArray();
        if (!block.IsBlock || !block.Values.IsEmpty || block.Children.Any(section => section.Name is not ("input" or "output" or "emancipate type")) ||
            types.Length != 1 || types[0].IsBlock || !types[0].Children.IsEmpty ||
            types[0].Values.Length != 1 || types[0].Values[0].Type != 0 || types[0].Values[0].Value < 0)
            return null;
        var inputs = Pairs("input");
        var outputs = Pairs("output");
        return inputs is null || outputs is null ? null : new(types[0].Values[0].Value, inputs.Value, outputs.Value);

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
