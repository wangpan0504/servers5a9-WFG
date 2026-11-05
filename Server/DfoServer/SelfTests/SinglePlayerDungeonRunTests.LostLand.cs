using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static partial class SinglePlayerDungeonRunTests
{
    static void VerifyLostLandClock()
    {
        VerifyLostLandWarpActions();
        var time = new LostLandTestClock();
        var clock = new LostLandRunClock(new(1200), time);
        time.Advance(TimeSpan.FromHours(1));
        That(clock.Remaining.TotalSeconds == LostLandAdmission.InitialSeconds && !clock.TryExpire(), "遗忘之地加载前不消耗时限");
        That(clock.Start() && !clock.Start(), "首个加载仅启动一次计时");
        time.Advance(TimeSpan.FromMilliseconds(LostLandAdmission.InitialSeconds * 1000 - 1));
        That(!clock.Elapsed && !clock.TryExpire() && clock.Remaining == TimeSpan.FromMilliseconds(1), "1800秒之前不提前结束");
        time.Advance(TimeSpan.FromMilliseconds(1));
        That(clock.Elapsed && clock.TryExpire() && !clock.TryExpire() && !clock.Start(), "1800秒边界只结束一次，不能重启");

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

        var route = new DungeonFixedRoute([Plan(start, 10, 13), Plan(middle, 11), BossPlan(boss, 12)],
            [new(start.Position, middle.Position), new(middle.Position, boss.Position)]);
        var authority = new FixtureCombatAuthority();
        That(SinglePlayerDungeonRun.TryCreate(entry, 1, 100, route, authority, out var created, out _, time), "专用正式Run接受完整资源路线");
        var run = created!;
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        run.LostLandClock!.Start();
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 10, 1));
        time.Advance(TimeSpan.FromSeconds(100));
        var beforeMove = run.Snapshot.Identity;
        That(run.Move(beforeMove, Guid.NewGuid(), boss.Position).Outcome == DungeonRunOutcome.Rejected &&
            run.Move(beforeMove with { Epoch = beforeMove.Epoch - 1 }, Guid.NewGuid(), middle.Position).Outcome == DungeonRunOutcome.Rejected,
            "遗忘之地带怪换房仍拒绝未连接目标和旧身份");
        That(run.Move(beforeMove, Guid.NewGuid(), middle.Position).Outcome == DungeonRunOutcome.Accepted,
            "遗忘之地未清完阻塞怪也可沿真实普通门换房");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), start.Position) is
            { Outcome: DungeonRunOutcome.Accepted, Snapshot.MapPayload: DungeonMapPayload.Cached } &&
            run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 13 }),
            "从未清怪的邻房回访保留原怪物key和已确认死亡，不重建演员");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), middle.Position) is
            { Outcome: DungeonRunOutcome.Accepted, Snapshot.MapPayload: DungeonMapPayload.Cached }, "带怪往返继续复用原邻房缓存");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(!run.LostLandClock.Start() && run.LostLandClock.Remaining.TotalSeconds == LostLandAdmission.InitialSeconds - 100, "过房及重复加载不能重置总时长");
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 11, 2));
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss.Position);
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 12, 3));
        That(run.ClearFact is null && run.Phase == SinglePlayerDungeonPhase.Dungeon, "合法Boss击杀不结束限时模式，不生成普通通关事实");
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), middle.Position);
        time.Advance(TimeSpan.FromSeconds(LostLandAdmission.InitialSeconds - 100));
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
        var warpEntry = entry with { Maze = maze with { WarpMaps = [new(start.Position,boss.Position)] } };
        var warp = new DungeonLostLandWarp(0,37044,"passiveobject/warp.obj","passiveobject/warp.act",boss.Position,714,323);
        var source = route.Rooms[0];
        var warpRoute = route with { Rooms = [source with { Room = source.Room with { LostLandWarps = [warp] } }, .. route.Rooms.Skip(1)] };
        foreach (var candidate in new[] { (Entry: entry,Route: warpRoute), (Entry: warpEntry,Route: route),
            (Entry: warpEntry with { Maze = warpEntry.Maze with { WarpMaps = [new(boss.Position,start.Position)] } },Route: warpRoute) })
        {
            That(SinglePlayerDungeonRun.TryCreate(candidate.Entry,4,400,candidate.Route,authority,out var denied,out _), "传送负向夹具保留合法普通路线");
            denied!.CompleteLoading(denied.Snapshot.Identity,Guid.NewGuid());
            That(denied.Move(denied.Snapshot.Identity,Guid.NewGuid(),boss.Position).Outcome == DungeonRunOutcome.Rejected,
                "只有DGN有向声明或只有本房无条件ACT均不足以跨房，反向声明也不能借用");
        }
        That(SinglePlayerDungeonRun.TryCreate(warpEntry,5,500,warpRoute,authority,out var warpRun,out _), "两份传送依据冻结在原Run");
        warpRun!.CompleteLoading(warpRun.Snapshot.Identity,Guid.NewGuid());
        That(warpRun.Move(warpRun.Snapshot.Identity,Guid.NewGuid(),boss.Position).Outcome == DungeonRunOutcome.Accepted &&
            warpRun.Snapshot.CurrentMap == boss && warpRun.ClearFact is null, "带怪跨格传送只换房，不清怪或生成通关事实");
        That(SinglePlayerDungeonRun.TryCreate(entry, 3, 300, route with { Rooms = [Plan(start, 10, 13), .. route.Rooms.Skip(1)] },
            authority, out var late, out _, time), "到期事务边界使用独立正式Run");
        late!.CompleteLoading(late.Snapshot.Identity, Guid.NewGuid());
        late.LostLandClock!.Start();
        var pending = late.PrepareConfirmedDeath(authority.Death(late.Snapshot.Identity, 10, 1)).Pending!;
        That(late.Move(late.Snapshot.Identity, Guid.NewGuid(), middle.Position).Outcome == DungeonRunOutcome.Rejected,
            "遗忘之地带怪换房不能越过尚未提交的死亡事务");
        time.Advance(TimeSpan.FromSeconds(LostLandAdmission.InitialSeconds));
        That(late.ConfirmCommittedDeath(pending).Outcome == DungeonRunOutcome.Accepted &&
            late.CurrentLivingMonster(13) is null &&
            late.PrepareConfirmedDeath(authority.Death(late.Snapshot.Identity, 13, 2)).Outcome == DungeonRunOutcome.Rejected &&
            late.Return(late.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
            "截止前已准备的真实事务可按原回执确认，但到期后新的存活怪死亡不得获得资格，仍可退出");
    }

    static void VerifyLostLandWarpActions()
    {
        var strings = new List<string>();
        PvfToken Tag(string value) { strings.Add("["+value+"]"); return new(3,strings.Count-1); }
        PvfToken Text(string value) { strings.Add(value); return new(6,strings.Count-1); }
        string Resolve(int id) => strings[id];
        PvfToken[] action = [Tag("MOTION"),Tag("BASE ANI"),Text("../animation/gate.ani"),Tag("/MOTION"),
            Tag("TRIGGER"),Tag("WHICH"),Tag("PLAYER"),Tag("CHECKUP"),Tag("IS IN AREA"),Tag("POS"),new(0,0),new(0,0),new(0,0),
            Tag("RANGE"),new(0,40),new(0,40),new(0,0),Tag("COORDINATE"),Text("RELATIVE"),Tag("/IS IN AREA"),Tag("/CHECKUP"),
            Tag("DO BEHAVIOR"),Tag("ME"),new(0,0),Tag("/TRIGGER"),Tag("BEHAVIOR"),Tag("MOVE MAP"),
            new(0,1),new(0,7),new(0,714),new(0,323),new(0,0),Tag("/MOVE MAP"),Tag("/BEHAVIOR")];
        That(DungeonLostLandWarpRules.UnconditionalMove(action,Resolve) == (new DungeonPoint(1,7),714,323),
            "无条件接近传送保留目标格和落点，末参数0不猜作wire kind");
        foreach (int at in new[] { 6,14,18,23,27,28,29,30,31 })
        {
            var invalid = action.ToArray();
            invalid[at] = at switch { 6 => Tag("MONSTER"), 14 => new(0,41), 18 => Text("ABSOLUTE"), 23 => new(0,1),
                27 => new(0,-1), 28 => new(0,256), 29 => new(0,-1), 30 => new(0,65536), _ => new(0,1) };
            That(DungeonLostLandWarpRules.UnconditionalMove(invalid,Resolve) is null,
                "未知主体、区域、坐标、行为或场景参数均不开放传送");
        }
        foreach (var invalid in new[] { action[..^1],action.Concat(action).ToArray(),
            action.Concat(new[] { Tag("TRIGGER"),Tag("DUNGEON SYMBOL COMPARE"),Text("lab_door_close"),Tag("/TRIGGER") }).ToArray() })
            That(DungeonLostLandWarpRules.UnconditionalMove(invalid,Resolve) is null,
                "截断、重复和附带机关或昼夜状态的ACT不能当作无条件传送");
        PvfToken[] whirlwind = [Tag("MOTION"),Tag("BASE ANI"),Text("spin.ani"),Tag("SOUND"),
            Text("wave"),new(0,2),Text("wave"),new(0,13),Text("wave"),new(0,25),Text("wave"),new(0,36),
            Text("wave"),new(0,48),Text("wave"),new(0,59),Tag("/SOUND"),Tag("/MOTION"),
            Tag("TRIGGER"),Tag("WHICH"),Tag("PLAYER"),Tag("CHECKUP"),Tag("DISTANCE"),Tag("<="),new(0,50),
            Tag("/CHECKUP"),Tag("DO BEHAVIOR"),Tag("CHECKUP OBJECT"),new(0,0),Tag("/TRIGGER"),
            Tag("BEHAVIOR"),Tag("SET SPEED"),Tag("Z AXIS"),new(0,2000),Tag("USE MY DIRECTION"),Tag("/SET SPEED"),Tag("/BEHAVIOR"),
            Tag("TRIGGER"),Tag("WHICH"),Tag("PLAYER"),Tag("CHECKUP"),Tag("ZPOS"),Tag(">="),new(0,650),Tag("/CHECKUP"),
            Tag("CHECKED NO"),Tag(">="),new(0,1),Tag("DO BEHAVIOR"),Tag("ME"),new(0,1),Tag("DO BEHAVIOR"),Tag("ME"),new(0,2),Tag("/TRIGGER"),
            Tag("BEHAVIOR"),Tag("MOVE MAP"),new(0,7),new(0,9),new(0,384),new(0,346),new(0,550),Tag("IS STATE CHECK"),new(0,0),
            Tag("/MOVE MAP"),Tag("/BEHAVIOR"),Tag("TRIGGER"),Tag("FRAME"),new(0,47),new(0,47),Tag("DO BEHAVIOR"),Tag("ME"),new(0,2),Tag("/TRIGGER"),
            Tag("BEHAVIOR"),Tag("SET ACTION"),Tag("CUSTOM"),new(0,1),Tag("NOW"),Tag("/BEHAVIOR")];
        That(DungeonLostLandWhirlwind.Move(whirlwind,Resolve) == (new DungeonPoint(7,9),384,346) &&
            DungeonLostLandWarpRules.UnconditionalMove(whirlwind,Resolve) is null,
            "水旋风保留本地距离、抬升和结束分支，不能当作无条件传送");
        foreach (int at in new[] { 20,24,33,43,50,53,57,58,59,60,61,63,77 })
        {
            var invalid = whirlwind.ToArray();
            invalid[at] = at switch { 20 => Tag("MONSTER"),57 or 59 => new(0,-1),58 => new(0,256),60 => new(0,65536),
                _ => new(0,whirlwind[at].Value+1) };
            That(DungeonLostLandWhirlwind.Move(invalid,Resolve) is null,
                "未知旋风主体、抬升条件、行为索引、目标边界或末参数不授予导航");
        }
        foreach (var invalid in new[] { whirlwind[..^1],whirlwind.Concat(whirlwind).ToArray(),
            whirlwind.Concat(new[] { Tag("TRIGGER"),Tag("DUNGEON SYMBOL COMPARE"),Text("lab_door_close"),Tag("/TRIGGER") }).ToArray() })
            That(DungeonLostLandWhirlwind.Move(invalid,Resolve) is null,"不完整或附加机关的水旋风ACT仍拒绝");

        PvfToken[] rescue = [Tag("MOTION"),Tag("BASE ANI"),Text("../animation/Event1.ani"),Tag("SPEECH"),Text("返程对话"),
            new(0,1),new(0,100),Tag("/SPEECH"),Tag("/MOTION"),
            Tag("TRIGGER"),Tag("FRAME"),new(0,3),new(0,3),Tag("DO BEHAVIOR"),Tag("ME"),new(0,0),Tag("/TRIGGER"),
            Tag("BEHAVIOR"),Tag("DUNGEON SYMBOL SET"),Text("Event"),new(0,1),Tag("DIALOG"),Text("获救对话"),new(0,30000),Tag("/BEHAVIOR"),
            Tag("TRIGGER"),Tag("FRAME"),new(0,26),new(0,26),Tag("DO BEHAVIOR"),Tag("ME"),new(0,1),Tag("/TRIGGER"),
            Tag("BEHAVIOR"),Tag("MOVE MAP"),new(0,9),new(0,6),new(0,376),new(0,320),new(0,0),
            Tag("IS STATE CHECK"),new(0,0),Tag("/MOVE MAP"),Tag("/BEHAVIOR"),
            Tag("TRIGGER"),Tag("FRAME"),new(0,27),new(0,27),Tag("DO BEHAVIOR"),Tag("ME"),new(0,2),Tag("/TRIGGER"),
            Tag("BEHAVIOR"),Tag("DESTROY"),Tag("/BEHAVIOR"),
            Tag("TRIGGER"),Tag("LIMIT"),new(0,1),Tag("FRAME"),new(0,20),new(0,20),Tag("WHICH"),Tag("PLAYER"),
            Tag("DO BEHAVIOR"),Tag("CHECKUP OBJECT"),new(0,3),Tag("/TRIGGER"),
            Tag("TRIGGER"),Tag("LIMIT"),new(0,1),Tag("FRAME"),new(0,21),new(0,21),Tag("WHICH"),Tag("PLAYER"),
            Tag("DO BEHAVIOR"),Tag("CHECKUP OBJECT"),new(0,3),Tag("/TRIGGER"),
            Tag("BEHAVIOR"),Tag("CREATE PASSIVEOBJECT"),Tag("INDEX"),new(0,37253),Tag("PARTICLE FILENAME"),Text(""),
            Tag("LEVEL"),new(0,1),Tag("POS"),new(0,0),new(0,0),new(0,130),Tag("/CREATE PASSIVEOBJECT"),Tag("/BEHAVIOR")];
        That(DungeonLostLandRescue.Move(rescue,Resolve) == (new DungeonPoint(9,6),376,320) &&
            DungeonLostLandWarpRules.UnconditionalMove(rescue,Resolve) is null,
            "公主救援完整动作链保留返程目标和落点，不能当作无条件接近传送");
        foreach (int at in new[] { 19,20,27,31,35,36,37,38,39,41,50,62,65,82 })
        {
            var invalid = rescue.ToArray();
            invalid[at] = at switch { 19 => Text("OtherEvent"),62 => Tag("MONSTER"),35 or 37 => new(0,-1),
                36 => new(0,256),38 => new(0,65536),_ => new(0,rescue[at].Value+1) };
            That(DungeonLostLandRescue.Move(invalid,Resolve) is null,
                "救援符号、帧、行为索引、主体、目标边界或展示物件变化不能借用原返程链");
        }
        foreach (var invalid in new[] { rescue[..^1],rescue.Concat(rescue).ToArray(),rescue.Append(new PvfToken(0,1)).ToArray() })
            That(DungeonLostLandRescue.Move(invalid,Resolve) is null,"救援动作链不完整或附加未知条件仍拒绝");
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
