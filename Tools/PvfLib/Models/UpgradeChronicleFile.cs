using System.Collections.Immutable;

namespace PvfLib;

// 设计图升级配方。一张设计图可以有多组 [emancipate group]，每组对应一个装备部位：
//   [input item]  三元组「道具 id、数量、输入槽位」，槽位 0 是可变装备，其余槽位是固定材料
//   [output item] 二元组「道具 id、数量」
// 这里只还原声明结构，是否允许升级、保留哪些强化信息由玩法层判断。
public sealed record EmancipateEntry(int ItemId, int Count, int Slot);
public sealed record EmancipateGroup(int Index, ImmutableArray<EmancipateEntry> Inputs, ImmutableArray<EmancipateEntry> Outputs);

public sealed record UpgradeChronicleFile(int TemplateId, string ScriptPath,
    ImmutableArray<EmancipateGroup> Groups, ImmutableArray<string> Diagnostics)
{
    public static UpgradeChronicleFile Parse(int id, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var errors = document.Diagnostics.ToList();
        var groups = ImmutableArray.CreateBuilder<EmancipateGroup>();
        foreach (var section in document.Sections.Where(section => section.Name == "emancipate group"))
        {
            if (section.Values.Length != 1 || section.Values[0].Type != 0)
            {
                errors.Add("设计图配方组缺少序号");
                continue;
            }
            var inputs = section.Children.SingleOrDefault(child => child.Name == "input item");
            var outputs = section.Children.SingleOrDefault(child => child.Name == "output item");
            if (inputs is null || outputs is null || inputs.Values.Any(token => token.Type != 0) ||
                outputs.Values.Any(token => token.Type != 0) || inputs.Values.Length % 3 != 0 || outputs.Values.Length % 2 != 0)
            {
                errors.Add("设计图配方组的输入输出声明不完整");
                continue;
            }
            var entries = ImmutableArray.CreateBuilder<EmancipateEntry>();
            for (int at = 0; at < inputs.Values.Length; at += 3)
                entries.Add(new(inputs.Values[at].Value, inputs.Values[at + 1].Value, inputs.Values[at + 2].Value));
            var results = ImmutableArray.CreateBuilder<EmancipateEntry>();
            for (int at = 0; at < outputs.Values.Length; at += 2)
                results.Add(new(outputs.Values[at].Value, outputs.Values[at + 1].Value, 0));
            groups.Add(new(section.Values[0].Value, entries.ToImmutable(), results.ToImmutable()));
        }
        return new(id, path, groups.ToImmutable(), errors.ToImmutableArray());
    }
}
