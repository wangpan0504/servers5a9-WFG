using System.Collections.Immutable;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Game.Adventure;

internal sealed record AdventureProgressionCommit(AdventureState State, ulong MaximumLevelExperience);

internal static class AdventureProgression
{
    // 与商品类别一致：0 为勇者结晶，1 为荣耀结晶。
    public const byte BraveShop = 0;

    // 角色已满级时整笔经验都溢出；跨过上限的那一单只取上限入口线以上的差额。
    // 熟练度仍复用这条判定；冒险团经验已改由 AdventureCredit 按有效里程计算。
    public static ulong MaximumLevelExperience(int level, ulong experience, uint gained, int maximumLevel, ulong capEntryExperience)
    {
        if (gained == 0 || level < 1 || level > maximumLevel) return 0;
        if (level >= maximumLevel) return gained;
        ulong total = checked(experience + gained);
        return total > capEntryExperience ? total - capEntryExperience : 0;
    }

    // credit 是已按国服95模型折算好的精确经验增量（AdventureCredit.Value 的差）；
    // rawOverflow 是同一段的原始溢出经验，只用于勇者结晶与成长胶囊。
    // 小数部分跨多次结算保留在 Remainder，避免每单截断丢失。
    public static AdventureState Advance(AdventureState before, decimal credit, ulong rawOverflow, AdventureCatalog catalog)
    {
        if (credit == 0 && rawOverflow == 0) return before;
        if (credit < 0) throw new InvalidDataException("冒险团计入经验不能为负");
        decimal scaled = credit + before.Remainder;
        ulong gained;
        try { gained = (ulong)decimal.Floor(scaled); }
        catch (OverflowException) { throw new InvalidDataException("冒险团经验超出范围"); }
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
            ulong total = checked(brave + rawOverflow);
            ulong steps = Math.Min(uint.MaxValue, total / shop.ExperiencePerPoint);
            brave = total % shop.ExperiencePerPoint;
            ulong add = Math.Min(uint.MaxValue, steps * shop.PointsPerStep);
            points[BraveShop] = (uint)ulong.Min(shop.Maximum, points[BraveShop] + add);
        }
        ulong capsule = ulong.Min(catalog.CapsuleCapacity, checked(before.CapsuleExperience + rawOverflow));
        var after = before with
        {
            Level = level, Experience = experience, Remainder = remainder,
            BraveExperience = brave, CapsuleExperience = capsule, Points = points.ToImmutable()
        };
        after.Validate(catalog);
        return after;
    }
}
