using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// Runtime装配已解析的只读规则；死亡冻结时不读取PVF，拾取及重试不重新抽取。
internal sealed class EquipmentAmplificationDropPolicy(AmplificationRules rules)
{
    public bool Eligible(ItemTemplate template) => template.Kind == ItemKind.Equipment && template.MinimumLevel >= 55 &&
        template.EquipmentSlot is 12 or >= 14 and <= 23 or 25 && template.Rarity >= 2 &&
        template.Rarity < rules.GenerationRates.Length && rules.GradeCorrections.TryGetValue(template.Rarity, out int correction) &&
        Math.Max(0, template.Grade - correction) >= rules.MinimumLevel;

    public ItemCore Apply(ItemTemplate template, ItemCore core, Func<int, int> draw)
    {
        if (!Eligible(template) || core.AmplifyType != 0 || core.AmplifyValue != 0 || core.Durability < template.Durability)
            return core;
        if (Sample() >= rules.GenerationRates[template.Rarity]) return core;
        int selected = Sample(), cumulative = 0;
        foreach (var option in rules.GenerationOptions)
        {
            cumulative += option.Weight;
            if (selected >= cumulative) continue;
            ushort value = rules.BaseValue(option.Type, template.Rarity);
            if (value == 0) return core;
            // pollution禁用只免除未净化状态，不免除生成的异次元属性，沿旧端生成器语义。
            byte type = template.AmplifyPollutionForbidden ? option.Type : (byte)(option.Type | 0x80);
            return core with { AmplifyType = type, AmplifyValue = value };
        }
        return core; // 权重不足10000时保留原生空区间，不归一化或补抽。

        int Sample()
        {
            int value = draw(10000);
            return value is >= 0 and < 10000 ? value : throw new InvalidDataException("异界气息掉落随机值越界");
        }
    }
}
