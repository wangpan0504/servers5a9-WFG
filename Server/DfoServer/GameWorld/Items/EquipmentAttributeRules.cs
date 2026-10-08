namespace DfoServer.GameWorld.Items;

internal readonly record struct EquipmentExplicitQuality(byte Bucket, byte Value);

internal static class EquipmentAttributeRules
{
    // 0207E497..0207E760分别处理654/65C；06309DF0/DF8同为range10/cap999。
    // 两项按声明原序独立评估，02958793..029589EE分别应用到能力690/698，不猜分量业务名。
    public static EquipmentAttributePair GetNormalDefaultSeparateAttackQuality(EquipmentAttributePair declaration, int seed) =>
        new(GetNormalDefaultQualityValue(declaration.First, seed), GetNormalDefaultQualityValue(declaration.Second, seed));

    // 匹配一般装备攻击/防御的normal参数：06309D70/80=10，06309D78/88=999。
    // 0206DBC0显式品质分支；输入已完成base/add合并且调用方核对完整Core和资源门禁。
    public static int GetNormalDefaultQualityValue(int mergedValue, int seed)
    {
        if (!TryGetExplicitQuality(seed, out var quality))
            throw new ArgumentOutOfRangeException(nameof(seed), "当前普通品质切片只闭合显式Create品质来源");
        const int rangePercent = 10;
        const int maximumDelta = 999;
        float range = (float)mergedValue * rangePercent / 100f;
        int delta = Math.Min(maximumDelta, NativeRound(range));
        float weighted = (float)quality.Value / 100f * (2 * delta);
        return checked(mergedValue - delta + NativeRound(weighted));

        static int NativeRound(float value)
        {
            // 0290DC30的0.5分量与cvttss2si；不是CHR的负值对称舍入。
            float result = value + 0.5f;
            if (!float.IsFinite(result) || (double)result < int.MinValue || (double)result > int.MaxValue)
                throw new InvalidDataException("装备品质中间值超过原生支持范围");
            return checked((int)result);
        }
    }

    // 普通模式中02950610→0294DF50的明确分量；模式cap不由这个入口代办。
    // rarity仅闭合当前初装与3145装备奖所需的0/1，不向其他稀有度填默认。
    public static int GetNormalAdjustedGrade(int templateGrade, int rarity, byte attr, byte emancipateLevel)
    {
        int rarityModifier = rarity switch
        {
            0 => -1,
            1 => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(rarity), "普通属性切片未闭合此稀有度")
        };
        // 027F8703按signed byte读取raw offset88，移动/保存仍保留原始byte位模式。
        int levelCorrection = unchecked((sbyte)emancipateLevel);
        return checked(templateGrade + (attr & 0x1f) / 3 + rarityModifier + levelCorrection);
    }

    // 0411289D及0411299E分别累加两项；调用方须先确认资源先base后add。
    // 这里只还原声明合并，不处理品质、强化、附魔、增幅或角色精通。
    public static EquipmentAttributePair AddDeclaration(EquipmentAttributePair basis, EquipmentAttributePair addition) =>
        new(checked(basis.First + addition.First), checked(basis.Second + addition.Second));

    public static bool TryGetExplicitQuality(int seed, out EquipmentExplicitQuality quality)
    {
        // 当前普通Create来源：02937E00特殊桶4，03FA9940特殊品质值100。
        // 与通用随机seed路径分开，不能以seed/1e9转换或猜其他特殊值。
        if (seed == 999999998)
        {
            quality = new(4, 100);
            return true;
        }
        quality = default;
        return false;
    }
}
