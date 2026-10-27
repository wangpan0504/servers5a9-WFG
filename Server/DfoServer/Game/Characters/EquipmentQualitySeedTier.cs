namespace DfoServer.Game.Characters;

internal sealed record EquipmentQualitySeedTier(int Seed, int Tier, int? FixedTargetPercent)
{
    // 客户端显示百分比来自实际属性集合平均；仅seed不能还原，固定目标也不冒称已显示。
    public int? DisplayPercent => null;
}

// 标准12槽装备seed档位建设分的只读数学依据，不审核消费历史，不赋予安装能力。
internal static class EquipmentQualitySeedResolver
{
    public static EquipmentQualitySeedTier Resolve(int seed)
    {
        // 匹配ARAD 03FA9940/02937E00的独立旧常量与连续固定区，不能把PVF Grade当品质。
        int? percent = seed switch
        {
            999_999_993 => 20,
            999_999_995 => 80,
            999_999_996 => 40,
            999_999_998 => 100,
            999_999_999 => 60,
            >= 999_999_894 and <= 999_999_992 => seed - 999_999_893,
            _ => null
        };
        if (percent is { } p) return new(seed, Math.Min(p / 20, 4), p);
        // 0369FC00：三步uint32回绕，取11/10/10位拼非负31位。全部int32有定义。
        uint state = unchecked((uint)seed);
        uint first = Step(ref state) >> 16 & 0x7ff;
        uint second = Step(ref state) >> 16 & 0x3ff;
        uint third = Step(ref state) >> 16 & 0x3ff;
        uint random = (first << 20) ^ (second << 10) ^ third;
        return new(seed, (int)(random % 100 / 20), null);
    }

    static uint Step(ref uint state) => state = unchecked(state * 1_103_515_245u + 12_345u);
}
