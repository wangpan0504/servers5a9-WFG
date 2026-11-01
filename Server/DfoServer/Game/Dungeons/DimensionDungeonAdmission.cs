using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 入场冻结路线与个人额度；普通次数和隐藏出现次数均复用角色日周账本，随原入场事务提交。
internal sealed record DimensionDungeonAdmission(int DungeonId, int Tier, bool HiddenBoss,
    DungeonIncountPeriod Period, DungeonIncountUsage DailyBefore, DungeonIncountUsage HiddenBefore,
    DimensionDungeonCatalog Catalog, int ExtraEntries = 0)
{
    public DungeonIncountRow DailyRow => Catalog.DailyRow with { Limit = Catalog.BaseDailyLimit + ExtraEntries };

    // 旧版本曾把资源合计值当基础上限，导致同一业务日已用次数超过基础+追加。
    // 超出理论总上限的记录视为旧口径遗留，降至基础值；之后按新追加券重新计算可进入次数。
    public static DungeonIncountUsage NormalizeLegacyOverflow(DungeonIncountUsage usage, int baseLimit, int extraEntries)
    {
        int capacity = checked(baseLimit + Math.Max(0, extraEntries));
        return usage.DailyUsed > capacity ? usage with { DailyUsed = baseLimit } : usage;
    }
    public DungeonEntryIncount? HiddenCharge => HiddenBoss
        ? new(DimensionDungeonCatalog.Entrance, Period, Catalog.HiddenRow, [], true) { ExpectedUsage = HiddenBefore } : null;
}
