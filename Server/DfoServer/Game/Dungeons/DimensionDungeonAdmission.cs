using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 入场冻结路线与个人额度；普通次数和隐藏出现次数均复用角色日周账本，随原入场事务提交。
internal sealed record DimensionDungeonAdmission(int DungeonId, int Tier, bool HiddenBoss,
    DungeonIncountPeriod Period, DungeonIncountUsage DailyBefore, DungeonIncountUsage HiddenBefore,
    DimensionDungeonCatalog Catalog, int ExtraEntries = 0)
{
    public DungeonIncountRow DailyRow => Catalog.DailyRow with { Limit = Catalog.DailyLimit + ExtraEntries };
    public DungeonEntryIncount? HiddenCharge => HiddenBoss
        ? new(DimensionDungeonCatalog.Entrance, Period, Catalog.HiddenRow, [], true) { ExpectedUsage = HiddenBefore } : null;
}
