using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record EquipmentQualityBox(int MinimumPercent, int MaximumPercent, ImmutableArray<byte> EquipmentSlots,
    int? RequiredRarity = null, bool RejectTitle = false)
{
    public bool Fixed => MinimumPercent >= 0;

    public static EquipmentQualityBox? Parse(int id, PvfDocument script)
    {
        StackableActionDeclaration.TryRead(script, out var raw);
        var action = StackableActionCatalog.Arguments(raw);
        bool fixedRange = raw?.Label == "[fixed probability kaleido]";
        bool gold = action?.Label == "[gold kaleido box]" ||
            Regex.IsMatch(script.Section("stackable type"), @"^\s*`\[gold kaleido\]`\s+0\s*$");
        // 黄金箱以真实 stackable 声明识别；旧资源可无动作，也可同时声明普通 kaleido 动作。
        // 本版旧商城15/897没有action type；其余普通活动版本由PVF动作声明识别。
        // 大师级调整箱同样没有 kaleido 动作，只声明 [variable need material] 指向旧箱 15/897。
        if (!fixedRange && !gold && action?.Label != "[kaleido]" && !(id is 15 or 897 && !script.Has("action type")))
            return Master(script);
        var arguments = action?.Parameters;
        if (fixedRange && (arguments is not { Count: 2 } || arguments[0] < 1 || arguments[1] > 100 || arguments[0] > arguments[1]))
            throw new InvalidDataException("品级调整箱固定范围无效");
        string slotText = script.Section("usable equipment type");
        if (slotText.Length == 0)
            slotText = script.Section("usable equip type");
        var slots = Regex.Matches(slotText, @"\[([^\]]+)\]")
            .Select(match => ItemCatalog.Slot(match.Groups[1].Value)).Distinct().ToImmutableArray();
        if (gold)
            return new(100, 100, slots);
        return new(fixedRange ? arguments![0] : -1, fixedRange ? arguments![1] : -1, slots);
    }

    // 稀有/神器/传说/史诗/勇者大师箱：材料对是 15 与 897 的同一正数，品级和展示属性取 100，不改魔法封印。
    static EquipmentQualityBox? Master(PvfDocument script)
    {
        if (!script.Has("variable need material") ||
            !Regex.IsMatch(script.Section("stackable type"), @"^\s*`\[etc\]`\s+0\s*$")) return null;
        int[] count = PvfScript.Integers(script.Section("material count"));
        int[] materials = PvfScript.Integers(script.Section("materials"));
        if (count is not [1] || materials is not [15, var grade, 897, var echoed] || grade != echoed || grade <= 0)
            return null;
        int[] rarity = PvfScript.Integers(script.Section("rarity"));
        if (rarity is not [>= 0 and <= 6]) throw new InvalidDataException("品级调整箱稀有度无效");
        return new(100, 100, [], rarity[0], true);
    }

    public bool Allows(ItemTemplate equipment) => equipment.Kind == ItemKind.Equipment &&
        equipment.EquipmentSlot is >= 12 and <= 25 && (EquipmentSlots.IsEmpty || EquipmentSlots.Contains(equipment.EquipmentSlot)) &&
        (RequiredRarity is not int rarity || equipment.Rarity == rarity) &&
        !(RejectTitle && equipment.EquipmentSlot == 13);

    /// <summary>
    /// 品级百分比 → 实例品级种子。客户端 03FA9900/03FA9940 的固定映射：1..99 用 999999893+p，
    /// 100 用独立常量 999999998；GM 指定品级与调整箱固定范围走同一套换算。
    /// </summary>
    public static int SeedForPercent(int percent)
    {
        if (percent is < 1 or > 100) throw new InvalidDataException("品级百分比要在 1-100 之间");
        return percent == 100 ? 999_999_998 : 999_999_893 + percent;
    }

    public int Roll(Func<long, long> random)
    {
        // 03FA9900/03FA9940 只承认 1..99 → 999999893+p 和 100 → 999999998。
        // 区外种子解成 0%，结果窗顶部品级（最下级..最上级）不绘制。
        // 普通箱在 1..100 上均匀取值，再写成同一套种子，显示档和属性档一致。
        int percent = Fixed
            ? MinimumPercent + (int)Draw(MaximumPercent - MinimumPercent + 1)
            : 1 + (int)Draw(100);
        return SeedForPercent(percent);

        long Draw(long maximum)
        {
            long value = random(maximum);
            if (value < 0 || value >= maximum) throw new InvalidDataException("品级调整随机值越界");
            return value;
        }
    }
}
