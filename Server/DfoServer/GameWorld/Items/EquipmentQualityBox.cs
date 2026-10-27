using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record EquipmentQualityBox(int MinimumPercent, int MaximumPercent, ImmutableArray<byte> EquipmentSlots)
{
    public bool Fixed => MinimumPercent >= 0;

    public static EquipmentQualityBox? Parse(int id, PvfDocument script)
    {
        StackableActionDeclaration.TryRead(script, out var raw);
        var action = StackableActionCatalog.Arguments(raw);
        bool fixedRange = raw?.Label == "[fixed probability kaleido]";
        bool gold = action?.Label == "[gold kaleido box]";
        // 本版旧商城15/897没有action type；活动版本由PVF动作声明识别。黄金箱说明是取最高品级。
        if (!fixedRange && !gold && action?.Label != "[kaleido]" && !(id is 15 or 897 && !script.Has("action type"))) return null;
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

    public bool Allows(ItemTemplate equipment) => equipment.Kind == ItemKind.Equipment &&
        equipment.EquipmentSlot is >= 12 and <= 25 && (EquipmentSlots.IsEmpty || EquipmentSlots.Contains(equipment.EquipmentSlot));

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
        // 当前客户端03FA9900/03FA9940：1..99使用999999893+p，100使用独立常量。
        // 普通随机种子避开客户端的固定品级保留区，不强制重抽相同结果。
        if (!Fixed) return checked(1 + (int)Draw(999_999_892));
        int percent = MinimumPercent + (int)Draw(MaximumPercent - MinimumPercent + 1);
        return SeedForPercent(percent);

        long Draw(long maximum)
        {
            long value = random(maximum);
            if (value < 0 || value >= maximum) throw new InvalidDataException("品级调整随机值越界");
            return value;
        }
    }
}
