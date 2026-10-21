using System.Collections.Immutable;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Game.Adventure;

// 满级通关的溢出经验在同一结算事务里推进三项账号进度：冒险团等级/经验、勇者结晶与成长胶囊。
// 规划只读提交前快照；Before 由仓储在事务内重新读取核对，After 由仓储按同一规则重算，避免伪造增量。
internal sealed record AdventureProgressionPlan(AdventureSnapshot Before, AdventureState After,
    AdventureCatalog Catalog, ulong MaximumLevelExperience, DateTimeOffset Now);
internal sealed record AdventureProgressionCommit(AdventureState State, ulong MaximumLevelExperience);

internal static class AdventureProgression
{
    // 与商品类别一致：0 为勇者结晶，1 为荣耀结晶。
    public const byte BraveShop = 0;

    public static AdventureProgressionPlan Plan(AdventureSnapshot before, ulong maximumLevelExperience,
        AdventureCatalog catalog, DateTimeOffset now) =>
        new(before, Advance(before.State, maximumLevelExperience, catalog), catalog, maximumLevelExperience, now);

    // 只把达到角色等级上限之后的经验计入冒险团；跨过上限的那一单只取上限入口线以上的差额。
    // 已满级时整笔结算经验都溢出，与是否还能存入角色经验条无关。
    public static ulong MaximumLevelExperience(int level, ulong experience, uint gained, int maximumLevel, ulong capEntryExperience)
    {
        if (gained == 0 || level < 1 || level > maximumLevel) return 0;
        if (level >= maximumLevel) return gained;
        ulong total = checked(experience + gained);
        return total > capEntryExperience ? total - capEntryExperience : 0;
    }

    public static AdventureState Advance(AdventureState before, ulong overflow, AdventureCatalog catalog)
    {
        if (overflow == 0) return before;
        // 经验倍率来自资源；小数部分跨多次结算保留在 Remainder，避免每单截断丢失。
        decimal scaled = overflow * catalog.ExperienceRate + before.Remainder;
        ulong gained = (ulong)decimal.Floor(scaled);
        decimal remainder = scaled - gained;
        int level = before.Level;
        ulong experience = checked(before.Experience + gained);
        while (level < catalog.MaximumLevel && experience >= catalog.Experience[level + 1])
        {
            experience -= catalog.Experience[level + 1];
            level++;
        }
        var points = before.Points.ToBuilder();
        ulong brave = before.BraveExperience;
        if (catalog.Shops.TryGetValue(BraveShop, out var shop) && shop.ExperiencePerPoint > 0 && shop.PointsPerStep > 0)
        {
            // 兑换后的余数继续累积；可兑换步数与单次步长都按 32 位协议字段饱和，点数按商店上限封顶。
            ulong total = checked(brave + overflow);
            ulong steps = Math.Min(uint.MaxValue, total / shop.ExperiencePerPoint);
            brave = total % shop.ExperiencePerPoint;
            ulong add = Math.Min(uint.MaxValue, steps * shop.PointsPerStep);
            points[BraveShop] = (uint)ulong.Min(shop.Maximum, points[BraveShop] + add);
        }
        ulong capsule = ulong.Min(catalog.CapsuleCapacity, checked(before.CapsuleExperience + overflow));
        var after = before with
        {
            Level = level, Experience = experience, Remainder = remainder,
            BraveExperience = brave, CapsuleExperience = capsule, Points = points.ToImmutable()
        };
        after.Validate(catalog);
        return after;
    }
}
