using System.Collections.Immutable;
using System.Globalization;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record EquipmentResealConsumable(int MinimumLevel, int MaximumLevel, bool OneShot,
    ImmutableArray<int> Rarities)
{
    public bool Allows(ItemTemplate target) => target.MinimumLevel >= MinimumLevel && target.MinimumLevel <= MaximumLevel &&
        target.Rarity >= 0 && target.Rarity < Rarities.Length && Rarities[target.Rarity] == 1;

    internal static EquipmentResealConsumable? Parse(string path, PvfDocument script)
    {
        // 旧商城蜜蜡没有动作声明；有专用封装标签的新道具仍按实际脚本解析。
        bool legacy = path is "cash/candle_goldwax.stk" or "cash/candle_goldwax_1.stk";
        bool legacyOneShot = path == "10098001/10098656.stk";
        if (!legacy && !script.Has("reseal lv") && !script.Has("reseal oneshot")) return null;
        int[] levels = PvfScript.Integers(script.Section("reseal lv"));
        int[] rarities = PvfScript.Integers(script.Section("Rarity possible explain"));
        if (levels.Length is not (0 or 2) || levels.Length == 2 && (levels[0] < 1 || levels[0] > levels[1]) ||
            rarities.Length != 7 || rarities.Any(value => value is not (0 or 1)))
            throw new InvalidDataException("封装道具范围声明无效");
        return new(levels.Length == 0 ? 1 : levels[0], levels.Length == 0 ? 99 : levels[1],
            legacyOneShot || script.Has("reseal oneshot"), [.. rarities]);
    }
}

internal sealed class EquipmentResealCatalog
{
    readonly ImmutableArray<int> grades, rarity, extended, souls, soulWeights, limits;
    readonly ImmutableArray<float> constants;
    public EquipmentResealCatalog(PvfArchive pvf) : this(pvf.ReadText("etc/reseal.etc")) { }
    internal EquipmentResealCatalog(string text)
    {
        ImmutableArray<int> Read(string tag) => [.. PvfScript.Integers(PvfScript.Section(text, tag))];
        grades = Read("grade"); rarity = Read("rarity weight"); extended = Read("extended rarity weight");
        souls = Read("reseal soul index"); soulWeights = Read("soul cost weight"); limits = Read("reseal count limit");
        constants = [.. PvfScript.Section(text, "soul reseal constant").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => float.Parse(value, CultureInfo.InvariantCulture))];
        if (grades.Length == 0 || grades.Length % 3 != 0 || limits.Length == 0 || limits.Length % 3 != 0 ||
            rarity.Length != 7 || extended.Length != 7 || souls.Length != 14 || soulWeights.Length != 7 || constants.Length != 7 ||
            grades.Concat(rarity).Concat(extended).Concat(souls).Concat(soulWeights).Concat(limits).Any(value => value < 0) ||
            constants.Any(value => !float.IsFinite(value) || value < 0)) throw new InvalidDataException("装备封装费用表无效");
        foreach (var rows in new[] { grades, limits })
            for (int index = 0; index < rows.Length; index += 3)
                if (rows[index] > rows[index + 1] || index > 0 && rows[index] <= rows[index - 2])
                    throw new InvalidDataException("装备封装等级范围无效");
        for (int index = 0; index < 7; index++)
            if (souls[index * 2] != index || souls[index * 2 + 1] <= 0) throw new InvalidDataException("装备封装灵魂映射无效");
    }

    public int Limit(ItemTemplate target) => EquipmentTransportRule.RangeValue(limits, target.MinimumLevel);
    public int WaxCost(ItemTemplate target, int count) => Valid(target, count)
        ? checked(EquipmentTransportRule.RangeValue(grades, target.MinimumLevel) * rarity[target.Rarity] + count * extended[target.Rarity]) : 0;
    public ItemMaterialCost? SoulCost(ItemTemplate target, int count)
    {
        if (!Valid(target, count) || soulWeights[target.Rarity] == 0 || constants[target.Rarity] == 0) return null;
        // 客户端009D86D0逐次float32平方乘幂；使用当前次数，不能改成Math.Pow双精度或次数加一。
        float factor = 1, basis = constants[target.Rarity];
        for (int power = count; power > 0; power >>= 1)
        {
            if ((power & 1) != 0) factor *= basis;
            basis *= basis;
        }
        int weight = checked(EquipmentTransportRule.RangeValue(grades, target.MinimumLevel) * rarity[target.Rarity] *
            extended[target.Rarity] * soulWeights[target.Rarity]);
        double value = (double)factor * weight;
        return value is > 0 and <= int.MaxValue ? new(souls[target.Rarity * 2 + 1], (int)value) : null;
    }
    bool Valid(ItemTemplate target, int count) => target.Rarity is >= 0 and < 7 && count >= 0 && count < Limit(target) &&
        EquipmentTransportRule.RangeValue(grades, target.MinimumLevel) > 0;
}
