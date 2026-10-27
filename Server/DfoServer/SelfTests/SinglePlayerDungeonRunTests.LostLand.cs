using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer;

internal static partial class SinglePlayerDungeonRunTests
{
    static void VerifyLostLandClock()
    {
        var time = new LostLandTestClock();
        var clock = new LostLandRunClock(new(1200), time);
        time.Advance(TimeSpan.FromHours(1));
        That(clock.Remaining.TotalSeconds == 600 && !clock.TryExpire(), "遗忘之地加载前不消耗时限");
        That(clock.Start() && !clock.Start(), "首个加载仅启动一次计时");
        time.Advance(TimeSpan.FromMilliseconds(599999));
        That(!clock.Elapsed && !clock.TryExpire() && clock.Remaining == TimeSpan.FromMilliseconds(1), "600秒之前不提前结束");
        time.Advance(TimeSpan.FromMilliseconds(1));
        That(clock.Elapsed && clock.TryExpire() && !clock.TryExpire() && !clock.Start(), "600秒边界只结束一次，不能重启");

        var start = new DungeonMap("map", new(0, 0), 101, "map/lost-start.map");
        var middle = new DungeonMap("map", new(1, 0), 102, "map/lost-middle.map");
        var boss = new DungeonMap("boss", new(2, 0), 103, "map/lost-boss.map");
        var maze = new DungeonMaze(0, 3, 1, "BBFFEE", [start.Position], [boss.Position], [start, middle, boss], [])
            { IsLostLand = true, ClearConditions = [new("limit time", 600, 1200)] };
        var definition = new DungeonDefinition(4000, "dungeon/lost.dgn", 25, 95, [maze], [])
            { DungeonType = "lost land", DisableFatigueConsumption = true, DisableGiveupPenalty = true };
        var character = new DfoServer.Game.Characters.Character(99, 0, "LostLand", 0, new(40, 0, 845, 155), null) { Level = 95 };
        That(DungeonEntryPlan.TryCreate(definition, character, 4000, 0, 0, out var admitted, out _), "限时资源生成专用正式计划");
        var entry = admitted!;
        That(entry.LostLand?.OpaqueValue == 1200 && !entry.UsesFatigue && !entry.RequiresFatigueOwner &&
            !entry.AllowsCompanionAi && entry.DisableClearReward && !entry.IsTerminalMap(boss), "专用计划不借普通Boss通关、翻牌或疲劳");
        foreach (var invalid in new[] { definition with { Id = 3 }, definition with { DungeonType = "normal" },
            definition with { Mazes = [maze with { ClearConditions = [new("limit time", 600, 0)] }] },
            definition with { Mazes = [maze with { ClearConditions = [new("limit time", 1200, 1200)] }] } })
            That(!DungeonEntryPlan.TryCreate(invalid, character, (uint)invalid.Id, 0, 0, out _, out _), "不明时限形状或伪身份不能准入");
        foreach (var mode in new (uint Quest, byte Difficulty, bool Practice, bool Hell)[] { (0u, (byte)1, false, false),
            (0u, (byte)0, true, false), (0u, (byte)0, false, true) })
            That(!DungeonEntryPlan.TryCreate(definition, character, 4000, mode.Quest, mode.Difficulty, out _, out _,
                practice: mode.Practice, hellParty: mode.Hell), "其它模式不能借遗忘之地入口");

        var route = new DungeonFixedRoute([Plan(start, 10), Plan(middle, 11), BossPlan(boss, 12)],
            [new(start.Position, middle.Position), new(middle.Position, boss.Position)]);
        var authority = new FixtureCombatAuthority();
        That(SinglePlayerDungeonRun.TryCreate(entry, 1, 100, route, authority, out var created, out _, time), "专用正式Run接受完整资源路线");
        var run = created!;
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        run.LostLandClock!.Start();
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 10, 1));
        time.Advance(TimeSpan.FromSeconds(100));
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), middle.Position);
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(!run.LostLandClock.Start() && run.LostLandClock.Remaining.TotalSeconds == 500, "过房及重复加载不能重置总时长");
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 11, 2));
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss.Position);
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 12, 3));
        That(run.ClearFact is null && run.Phase == SinglePlayerDungeonPhase.Dungeon, "合法Boss击杀不结束限时模式，不生成普通通关事实");
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), middle.Position);
        time.Advance(TimeSpan.FromSeconds(500));
        That(run.Phase == SinglePlayerDungeonPhase.TimedOut && run.CurrentLivingMonster(11) is null &&
            run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected &&
            run.Move(run.Snapshot.Identity, Guid.NewGuid(), start.Position).Outcome == DungeonRunOutcome.Rejected && run.ClearFact is null,
            "到期即使正在加载也停止移动、加载和新死亡，不等待后台取得命令门");
        That(run.Return(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted &&
            run.Phase == SinglePlayerDungeonPhase.Returned, "到期仍沿原Return退出，不预先破坏回城状态");

        var disconnected = entry with { Maze = maze with { Greed = "BBEEII" } };
        That(SinglePlayerDungeonRun.TryCreate(disconnected, 2, 200, route with { Connections = [route.Connections[0]] }, authority,
            out _, out _), "独立Boss按原资源保留，不强求普通门全图可达");
        That(!SinglePlayerDungeonRun.TryCreate(disconnected, 2, 200, route, authority, out _, out _), "不能补出免费普通门连接独立房");
        That(SinglePlayerDungeonRun.TryCreate(entry, 3, 300, route with { Rooms = [Plan(start, 10, 13), .. route.Rooms.Skip(1)] },
            authority, out var late, out _, time), "到期事务边界使用独立正式Run");
        late!.CompleteLoading(late.Snapshot.Identity, Guid.NewGuid());
        late.LostLandClock!.Start();
        var pending = late.PrepareConfirmedDeath(authority.Death(late.Snapshot.Identity, 10, 1)).Pending!;
        time.Advance(TimeSpan.FromSeconds(600));
        That(late.ConfirmCommittedDeath(pending).Outcome == DungeonRunOutcome.Accepted &&
            late.CurrentLivingMonster(13) is null &&
            late.PrepareConfirmedDeath(authority.Death(late.Snapshot.Identity, 13, 2)).Outcome == DungeonRunOutcome.Rejected &&
            late.Return(late.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
            "截止前已准备的真实事务可按原回执确认，但到期后新的存活怪死亡不得获得资格，仍可退出");
    }
}

internal sealed class LostLandTestClock : TimeProvider
{
    long ticks;
    public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 8, 0, 0, TimeSpan.FromHours(8));
    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    public void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
}
