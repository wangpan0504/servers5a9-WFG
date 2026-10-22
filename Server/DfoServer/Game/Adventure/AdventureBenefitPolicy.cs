using DfoServer.GameWorld.Adventure;

namespace DfoServer.Game.Adventure;

// 冒险团等级属性的生效范围：本版只把可核验的「通关经验值增加」计入结算奖励。
// 四维由客户端按同一资源展示与计算，服务端不重复注入角色基础属性；
// 绝望之塔次数与攻坚掉落缺少对应系统或物品映射，保持未接入并在模块文档登记。
internal static class AdventureBenefitPolicy
{
    public const string ClearExperienceGrowth = "clear result exp up";

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
}
