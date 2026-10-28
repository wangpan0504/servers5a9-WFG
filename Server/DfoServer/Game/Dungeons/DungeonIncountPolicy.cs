using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 副本日/周进入次数（Etc/DungeonEtc/DungeonIncountInfo.etc）。
// 已证（客户端静态）：世界地图 018EC180 每日 48010“每日进入次数 %d/%d” = min(+0x6C68, +0x6C7C)/上限；
// 018EC6BD 起 67086“每周入场次数 %d/%d” = +0x6C7C/第二值（03FB9DC0），第二值 <=0 时 018EC6C6 跳过周显示。
// 两个剩余值只由 NOTI 550 写入角色对象（按角色），所以 [dungeon incount info] 按角色计数；计数、重置与拒绝全部由服务端决定。
// 扣次时机：PVF [sub incount on clear dungeon] 列出的副本（95 PVF 只有 7184）通关时扣，其余在入场提交时扣。
// 假设（客户端无读者，未证明）：
//   日界 = UTC+8 06:00（本服统一业务日，同 QuestCycle；客户端文本 33348/42796“每日凌晨6点初始化”）；
//   周界 = 周四 06:00（文本 68076/68109“每周四凌晨6点初始化”），[special weekly reset day] 的星期按 0=周日 编码；
//   账号表重置键查 [reset info]：值 -1 = 每日，0..6 = 每周该星期（PVF 0→-1、1→4 与“每周四”吻合）。
internal readonly record struct DungeonIncountUsage(int DailyUsed, int WeeklyUsed, int DailyExtra = 0, int WeeklyExtra = 0);
internal readonly record struct DungeonIncountPeriod(int Day, int Week);
internal readonly record struct StoredDungeonIncount(int Dungeon, DungeonIncountPeriod Period, DungeonIncountUsage Usage);
internal readonly record struct DungeonEnterCount(uint Dungeon, byte Daily, byte Weekly);
internal sealed record DungeonIncountCommit(Character Character, DungeonIncountUsage Usage);
// 账号次数一次记账：副本、当前周期键（每日为日键，每周为周起始日键）、上限；Check=false 为 [enter add incount dungeon index] 连带记次，不做拒绝判断。
internal readonly record struct AccountIncountCharge(int Dungeon, int Period, int Limit, bool Check);

internal static class DungeonIncountPolicy
{
    public static DungeonIncountPeriod Period(DateTimeOffset now, int weeklyResetDay = DungeonIncountCatalog.DefaultWeeklyResetDay)
    {
        var date = BusinessDate(now);
        return new(Key(date), WeekKey(date, weeklyResetDay));
    }

    // UTC+8 06:00 为日界：UTC 时间 +2 小时后的日期。
    static DateTime BusinessDate(DateTimeOffset now) => now.UtcDateTime.AddHours(2).Date;
    static int Key(DateTime day) => day.Year * 10000 + day.Month * 100 + day.Day;
    static int WeekKey(DateTime date, int weekday) => Key(date.AddDays(-(((int)date.DayOfWeek - weekday) % 7 + 7) % 7));

    // 账号行的周期键；[reset info] 缺键或值越界时 catalog 解析已拒绝。
    public static int AccountPeriod(DungeonIncountCatalog catalog, AccountIncountRow row, DateTimeOffset now)
    {
        int reset = catalog.ResetInfo[row.ResetType];
        var date = BusinessDate(now);
        return reset < 0 ? Key(date) : WeekKey(date, reset);
    }

    // 账号行的重置节奏来自 PVF [reset info]：日键或 0..6 任意周起点的周键。
    // 按账号刷新次数时不知道每行的重置类型，列出当前可能命中的全部周期键一次清零；
    // 非当前周期的旧行即便被列中也不会改变可读值（读侧本就按周期比对返回 0）。
    public static int[] CurrentAccountPeriods(DateTimeOffset now)
    {
        var date = BusinessDate(now);
        var keys = new int[8];
        keys[0] = Key(date);
        for (int weekday = 0; weekday < 7; weekday++) keys[weekday + 1] = WeekKey(date, weekday);
        return keys;
    }

    public static DungeonIncountUsage Current(DungeonIncountUsage stored, DungeonIncountPeriod storedPeriod, DungeonIncountPeriod now) =>
        new(storedPeriod.Day == now.Day ? stored.DailyUsed : 0, storedPeriod.Week == now.Week ? stored.WeeklyUsed : 0,
            storedPeriod.Day == now.Day ? stored.DailyExtra : 0, storedPeriod.Week == now.Week ? stored.WeeklyExtra : 0);

    public static int DailyRemaining(DungeonIncountRow row, DungeonIncountUsage usage) => Math.Max(0, checked(row.Limit + usage.DailyExtra) - usage.DailyUsed);
    // 无周上限（第二值 <=0）：第二个 u8 回填日剩余，使 min(+0x6C68,+0x6C7C) 仍等于日剩余。
    public static int WeeklyRemaining(DungeonIncountRow row, DungeonIncountUsage usage) =>
        row.WeeklyLimit > 0 ? Math.Max(0, checked(row.WeeklyLimit + usage.WeeklyExtra) - usage.WeeklyUsed) : DailyRemaining(row, usage);

    public static bool CanEnter(DungeonIncountRow row, DungeonIncountUsage usage) =>
        DailyRemaining(row, usage) > 0 && WeeklyRemaining(row, usage) > 0;

    // 同行每个副本一条 NOTI 550，剩余相同。
    public static IEnumerable<DungeonEnterCount> Views(DungeonIncountRow row, DungeonIncountUsage usage) =>
        row.Dungeons.Where(dungeon => dungeon > 0).Select(dungeon => View(checked((uint)dungeon), row, usage));

    public static DungeonEnterCount View(uint dungeon, DungeonIncountRow row, DungeonIncountUsage usage) =>
        new(dungeon, (byte)Math.Min(DailyRemaining(row, usage), byte.MaxValue), (byte)Math.Min(WeeklyRemaining(row, usage), byte.MaxValue));
}
