using System.Collections.Immutable;
using System.Text.RegularExpressions;
using static PvfLib.PvfScript;

namespace PvfLib;

// [recipe] 设计图的原始声明：
//   [input item]  二元组「道具 id、数量」列表，第一对是被改造的装备，其余是固定材料
//   [output item] 二元组「道具 id、数量」，本版配方只有一件产物
//   [maintain ability] / [maintain socket stat] 表示产物保留原实例的对应属性
// 这里只还原声明结构，准入与保留策略由玩法层判断。
public sealed record ItemRecipeEntry(int ItemId, int Count);

public sealed record ItemRecipeFile(int TemplateId, string ScriptPath, ImmutableArray<ItemRecipeEntry> Inputs,
    ItemRecipeEntry? Output, string? MaintainAbility, bool MaintainSocketStat, ImmutableArray<string> Diagnostics)
{
    public static ItemRecipeFile Parse(int id, string path, string text)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        if (!Section(text, "stackable type").Contains("[recipe]", StringComparison.Ordinal))
            errors.Add("非[recipe]堆叠类型");
        var inputs = Pairs(Section(text, "input item"), "配方输入", errors);
        var outputs = Pairs(Section(text, "output item"), "配方产物", errors);
        if (inputs.Length < 2)
            errors.Add("配方输入不足");
        if (outputs.Length != 1)
            errors.Add("配方产物必须唯一");
        var ability = Regex.Match(Section(text, "maintain ability"), @"`([^`]*)`");
        return new(id, path, inputs, outputs.Length == 1 ? outputs[0] : null,
            ability.Success ? ability.Groups[1].Value.Trim() : null,
            Regex.IsMatch(text, @"\[maintain socket stat\]", RegexOptions.IgnoreCase), errors.ToImmutable());
    }

    static ImmutableArray<ItemRecipeEntry> Pairs(string value, string name, ImmutableArray<string>.Builder errors)
    {
        var tokens = Regex.Matches(value, @"-?\d+").Select(match => match.Value).ToArray();
        if (tokens.Length == 0)
            return [];
        if (tokens.Length % 2 != 0)
        {
            errors.Add(name + "行宽为奇数");
            return [];
        }
        var entries = ImmutableArray.CreateBuilder<ItemRecipeEntry>();
        for (int at = 0; at < tokens.Length; at += 2)
        {
            if (!int.TryParse(tokens[at], out int itemId) || itemId <= 0 ||
                !int.TryParse(tokens[at + 1], out int count) || count <= 0)
            {
                errors.Add(name + "编号或数量无效");
                return [];
            }
            entries.Add(new(itemId, count));
        }
        return entries.ToImmutable();
    }
}
