using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record EquipmentQualityBox(int MinimumPercent, int MaximumPercent, ImmutableArray<byte> EquipmentSlots)
{
    public bool Fixed => MinimumPercent >= 0;

    public static EquipmentQualityBox? Parse(int id, PvfDocument script)
    {
        string action = Regex.Match(script.Section("action type"), @"\[([^\]]+)\]").Groups[1].Value;
        bool fixedRange = action == "fixed probability kaleido";
        bool gold = action == "gold kaleido box";
        // 本版旧商城15/897没有action type；活动版本由PVF动作声明识别。黄金箱说明是取最高品级。
        if (!fixedRange && !gold && action != "kaleido" && !(id is 15 or 897 && action.Length == 0)) return null;
        var arguments = PvfScript.Integers(script.Section("action type"));
        if (fixedRange && (arguments.Length != 2 || arguments[0] < 1 || arguments[1] > 100 || arguments[0] > arguments[1]))
            throw new InvalidDataException("品级调整箱固定范围无效");
        string slotText = script.Section("usable equipment type");
        if (slotText.Length == 0)
            slotText = script.Section("usable equip type");
        var slots = Regex.Matches(slotText, @"\[([^\]]+)\]")
            .Select(match => ItemCatalog.Slot(match.Groups[1].Value)).Distinct().ToImmutableArray();
        if (gold)
            return new(100, 100, slots);
        return new(fixedRange ? arguments[0] : -1, fixedRange ? arguments[1] : -1, slots);
    }

    public bool Allows(ItemTemplate equipment) => equipment.Kind == ItemKind.Equipment &&
        equipment.EquipmentSlot is >= 12 and <= 25 && (EquipmentSlots.IsEmpty || EquipmentSlots.Contains(equipment.EquipmentSlot));

    public int Roll(Func<long, long> random)
    {
        // 当前客户端03FA9900/03FA9940：1..99使用999999893+p，100使用独立常量。
        // 普通随机种子避开客户端的固定品级保留区，不强制重抽相同结果。
        if (!Fixed) return checked(1 + (int)Draw(999_999_892));
        int percent = MinimumPercent + (int)Draw(MaximumPercent - MinimumPercent + 1);
        return percent == 100 ? 999_999_998 : 999_999_893 + percent;

        long Draw(long maximum)
        {
            long value = random(maximum);
            if (value < 0 || value >= maximum) throw new InvalidDataException("品级调整随机值越界");
            return value;
        }
    }
}
