using DfoServer.GameWorld.Adventure;

namespace DfoServer.Game.Adventure;

// 冒险团等级属性的生效范围：除「通关经验值增加」外，同步接入绝望之塔额外进入次数与安徒恩魔能石掉落率加成。
// 四维由客户端按同一资源展示与计算，服务端不重复注入角色基础属性。
internal static class AdventureBenefitPolicy
{
    public const string ClearExperienceGrowth = "clear result exp up";
    public const string DespairTowerEnterCountEtc = "add tower of despair enter count";
    public const string AntoniumDropItem = "antonium drop";
    public const int AntoniumItemId = 10093974;

    // 资源逐级声明当前等级的总加成（Lv1无属性行），以基础通关经验为基数向下取整。
    public static uint ClearExperienceBonus(uint basisExperience, int level, AdventureCatalog catalog)
    {
        if (basisExperience == 0 || level < 1) return 0;
        if (!catalog.Benefits.TryGetValue(level, out var benefit) ||
            !benefit.Growth.TryGetValue(ClearExperienceGrowth, out float percent) || !float.IsFinite(percent) || percent <= 0)
            return 0;
        decimal bonus = decimal.Floor(basisExperience * (decimal)percent / 100m);
        return bonus >= uint.MaxValue ? uint.MaxValue : (uint)bonus;
    }

    public static int DespairTowerExtraEntries(int level, AdventureCatalog? catalog)
    {
        if (catalog is null || level < 1 || !catalog.Benefits.TryGetValue(level, out var benefit) ||
            !benefit.Etc.TryGetValue(DespairTowerEnterCountEtc, out float extra) || !float.IsFinite(extra) || extra <= 0)
            return 0;
        return extra >= int.MaxValue ? int.MaxValue : (int)Math.Floor(extra);
    }

    // [item] 20 "antonium drop" 以万分比（如 60 = +0.60%）声明当前冒险团等级对魔能石（10093974）独立掉落的加成。
    public static int AntoniumDropBasisPoints(int level, AdventureCatalog? catalog)
    {
        if (catalog is null || level < 1 || !catalog.Benefits.TryGetValue(level, out var benefit) ||
            !benefit.Items.TryGetValue(AntoniumDropItem, out float value) || !float.IsFinite(value) || value <= 0)
            return 0;
        return value >= int.MaxValue ? int.MaxValue : (int)Math.Floor(value);
    }
}
