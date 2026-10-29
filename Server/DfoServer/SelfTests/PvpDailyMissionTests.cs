using System.Collections.Immutable;
using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Pvp;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpDailyMissionTests
{
    internal static void RunCore()
    {
        int before = Count;
        var day = PvpMissionPeriod.At(DateTimeOffset.Parse("2026-10-29T05:59:59Z"), TimeZoneInfo.Utc);
        var next = PvpMissionPeriod.At(DateTimeOffset.Parse("2026-10-29T06:00:00Z"), TimeZoneInfo.Utc);
        That(next.Day == day.Day + 1 && day.End == next.Start && day.End - day.Start == 86400,
            "服务日以本地六点换日，查询整局时间使用秒及半开区间");
        var zone = TimeZoneInfo.CreateCustomTimeZone("PvpTest+8", TimeSpan.FromHours(8), "PvpTest+8", "PvpTest+8");
        That(PvpMissionPeriod.At(DateTimeOffset.Parse("2026-10-28T22:00:00Z"), zone).Day == next.Day,
            "日常使用注入时区而非主机UTC自然日");
        PvpMissionDefinition Definition(string condition, int parameter, int target, bool total = false) =>
            new(113, total ? (byte)4 : (byte)2, 0, 0, 30, -1, condition, total, null, parameter, target)
                { Group = 0, Rewards = [new(10092640, 40)] };
        PvpArenaResultRecord Match(PvpArenaOutcome outcome, bool total = false, byte? job = 1, uint kills = 0) =>
            new(Guid.NewGuid(), Guid.NewGuid(), 101, PvpArenaMode.Individual,
                [new("one", 1, 0, 0, outcome) { Kills = kills }, new("two", 2, 1, 1,
                    outcome == PvpArenaOutcome.Win ? PvpArenaOutcome.Lose : PvpArenaOutcome.Win) { Job = job }], false, 1)
                { RatingMode = total ? PvpRatingMode.Total : PvpRatingMode.Ordinary };
        var win = Match(PvpArenaOutcome.Win);
        var lose = Match(PvpArenaOutcome.Lose);
        var draw = Match(PvpArenaOutcome.Draw);
        var cancelled = Match(PvpArenaOutcome.Cancelled);
        That(PvpDailyMissionDay.Current(Definition("hereafter join", 4, 4), [win, lose, draw, cancelled], 1) == 3,
            "日常参赛包括平局，取消不计");
        That(PvpDailyMissionDay.Current(Definition("hereafter winning count", 2, 2), [win, lose, draw], 1) == 1,
            "胜场只消费本角色实际胜利");
        That(PvpDailyMissionDay.Current(Definition("winning job", 2, 2) with { Jobs = [1, 7] },
            [win, Match(PvpArenaOutcome.Win, job: 7), Match(PvpArenaOutcome.Win, job: null)], 1) == 2,
            "变长职业组匹配实际对手基础职业，旧事实缺职业不猜值");
        That(PvpDailyMissionDay.Current(Definition("winning job", 2, 2) with { Jobs = [7] }, [win, lose], 1) == 0,
            "职业不符或自己未获胜不能推进");
        var streak = Definition("winning streak", 2, 3);
        That(PvpDailyMissionDay.Current(streak, [win, win, win, win], 1) == 2 &&
            PvpDailyMissionDay.Current(streak, [win, lose, win, draw, win, win], 1) == 1,
            "本服按不重叠完整连胜段计次数，失败和平局中断未满段");
        That(PvpDailyMissionDay.Current(streak, [win, win, win, win, win, win, win], 1) == 3,
            "进度钳至真实目标次数，不把连胜长度当完成次数");
        That(PvpDailyMissionDay.Current(Definition("kill count", 9, 9, true),
            [Match(PvpArenaOutcome.Win, kills: 9), Match(PvpArenaOutcome.Lose, true, kills: 4), Match(PvpArenaOutcome.Win, true, kills: 3)], 1) == 7,
            "总力击杀只累加总力整局冻结击杀，普通击杀不混入");
        That(PvpDailyMissionDay.Current(Definition("hereafter join", 3, 3, true), [win, Match(PvpArenaOutcome.Win, true)], 1) == 1,
            "总力日常不使用普通参赛来源");
        var pool = (from grade in new byte[] { 2, 4 } from category in new[] { 0, 1, 2 }
                    select Definition("hereafter join", 3, 3, grade == 4) with
                    { Id = (ushort)(100 + grade * 10 + category), Grade = grade, Group = category }).ToImmutableArray();
        var allocated = PvpDailyMissionDay.Draw(day.Day, pool, 0);
        allocated.Validate(day.Day);
        That(allocated.Entries.Length == 6 && PvpDailyMissionDay.Same(allocated,
            System.Text.Json.JsonSerializer.Deserialize<PvpDailyMissionDay>(System.Text.Json.JsonSerializer.Serialize(allocated))),
            "分配JSON独立回读保持六项定义、奖励及领取状态，不能用数组引用比较");
        That(!PvpDailyMissionDay.Same(allocated, allocated with { Day = day.Day + 1 }), "不同服务日不可复用领取状态");
        Reject<InvalidDataException>(() => PvpDailyMissionDay.Draw(day.Day, pool.RemoveAt(0), 0), "缺候选明确失败，不伪造任务");
        byte[] wire = PvpMissionPackets.Daily(2, [new(2, 113, 2, 4, 2, 40)]);
        Bytes(wire, [2, 1, 113, 0, 2, 0, 0, 0, 4, 0, 0, 0, 2, 0, 0, 0, 40, 0, 0, 0],
            "579独立金样：grade/count/id/current/target/奖励key2/count，18字节行");
        Reject<InvalidDataException>(() => PvpMissionPackets.Daily(4, [new(2, 113, 0, 4, 2, 40)]), "禁止跨grade混发日常");
        Console.WriteLine($"PASS {Count - before} 决斗每日服务日、真实进度和579字节断言");
    }
}
