using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019RoomReplacementTests
{
    public static void RunCore()
    {
        int before = Count;
        ReplacementUsesOriginalRun();
        StaleAndForeignPreparation();
        InvalidTargetsAndResources();
        BatchPreflight();
        Console.WriteLine($"PASS {Count - before} 首期强者之路原Run下一房间真实替换");
    }

    static void ReplacementUsesOriginalRun()
    {
        var f = new Fixture();
        var before = f.Run.Snapshot;
        That(f.Prepare(out var prepared), "合法下一房间使用真实资源行准备替换");
        That(f.Run.Snapshot.Identity == before.Identity && f.Run.Entry.Maze.MapSpecification![1].Id == 2 &&
            prepared!.Plan.Room.Map is { Id: 400597, Kind: "map", Position: { X: 1, Y: 0 } } &&
            prepared.Plan.Monsters is [{ MonsterId: 109008143, ObjectKey: 400, SourceMap.Id: 400597 }],
            "准备不改原Run，新计划保留原坐标及资源真实怪物，不只改HUD编号");
        var applied = f.Run.ApplyStrongway2019NextRoom(prepared!);
        That(applied.Outcome == DungeonRunOutcome.Accepted && applied.Snapshot.Identity.InstanceId == before.Identity.InstanceId &&
            applied.Snapshot.Identity.TaskAttempt == before.Identity.TaskAttempt && applied.Snapshot.Identity.Epoch == before.Identity.Epoch + 1 &&
            applied.Snapshot.CurrentMap.Id == 1 && f.Run.Entry.Maze.MapSpecification![1] == prepared!.Plan.Room.Map &&
            f.Run.Entry.BossMap == f.Boss && prepared.NextObjectKey == 401,
            "在原实例原尝试提交替换，推进epoch但不提前进图，原Boss终末保持");
        That(f.Run.ApplyStrongway2019NextRoom(prepared!).Outcome == DungeonRunOutcome.Duplicate,
            "同一准备对象提交重放不再次变更房间或epoch");
        That(!f.Run.TryPrepareStrongway2019NextRoom(f.Run.Snapshot.Identity, Guid.NewGuid(), new(1, 0), f.Catalog,
            f.Resource, 400, out _, out _), "替换后已分配的演员key不能由下一次技能重新使用");
        f.KillCurrent();
        That(f.Run.Move(before.Identity, Guid.NewGuid(), new(1, 0)).Outcome == DungeonRunOutcome.Rejected,
            "替换前旧identity不能继续移动");
        f.Enter(new(1, 0));
        That(ReferenceEquals(f.Run.CurrentMonsterPlan.Room.Map, prepared!.Plan.Room.Map) &&
            f.Run.CurrentMonsterPlan.Monsters.Single().MonsterId == 109008143 &&
            f.Run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 400 }),
            "真实Move和加载使用新资源、怪物对象及原Run房间状态");
        That(f.Run.PrepareConfirmedDeath(f.Authority.Death(f.Run.Snapshot.Identity, 200)).Outcome == DungeonRunOutcome.Rejected,
            "旧中间房间演员key不再接受死亡上报");
        f.KillCurrent();
        That(f.Run.ClearFact is null && f.Run.Phase == SinglePlayerDungeonPhase.Dungeon,
            "候选地图即使包含Boss品级演员也不能夺取原副本终末");
        f.Enter(new(2, 0));
        var clear = f.KillCurrent();
        That(clear.ClearFact is { } fact && ReferenceEquals(f.Run.ClearFact, fact) && fact.ClearMap == f.Boss &&
            fact.Identity.InstanceId == before.Identity.InstanceId && fact.ConfirmedDeathEvents.Count == 3,
            "最终仍由原Run在原Boss真实死亡生成唯一原始ClearFact并保留三房死亡");
    }

    static void StaleAndForeignPreparation()
    {
        var f = new Fixture();
        That(f.Prepare(out var prepared), "准备原Run的下一房间候选");
        var foreign = new Fixture();
        That(foreign.Run.ApplyStrongway2019NextRoom(prepared!).Outcome == DungeonRunOutcome.Rejected,
            "另一原Run不能提交本实例准备对象");
        var clone = new PreparedStrongway2019RoomReplacement(f.Run, prepared!.Expected, prepared.ChangeId,
            prepared.Target, prepared.OriginalMap, prepared.Plan);
        That(!f.Run.CanApplyStrongway2019NextRoom(clone) && f.Run.ApplyStrongway2019NextRoom(clone).Outcome == DungeonRunOutcome.Rejected,
            "复制相同字段不能冒充原Run签发的原始准备对象");
        f.KillCurrent(); f.Enter(new(1, 0));
        var snapshot = f.Run.Snapshot;
        That(f.Run.ApplyStrongway2019NextRoom(prepared!).Outcome == DungeonRunOutcome.Rejected && SameScene(f.Run.Snapshot, snapshot),
            "准备后目标房间已进入，迟到提交不得覆盖真实已加载演员");
        That(!f.Run.TryPrepareStrongway2019NextRoom(f.Run.Snapshot.Identity, Guid.NewGuid(), new(1, 0), f.Catalog,
                f.Resource, 400, out _, out _), "不能替换当前或已经进入的房间");
        f.KillCurrent(); f.Enter(new(0, 0));
        That(!f.Prepare(out _), "离开已访问房间之后也不能把其加载和死亡事实覆盖为新房间");
    }

    static void InvalidTargetsAndResources()
    {
        var f = new Fixture(); var initial = f.Run.Snapshot;
        foreach (var (identity, target, resource, key) in new[]
        {
            (initial.Identity with { InstanceId = Guid.NewGuid() }, new DungeonPoint(1, 0), f.Resource, (ushort)400),
            (initial.Identity with { TaskAttempt = initial.Identity.TaskAttempt + 1 }, new DungeonPoint(1, 0), f.Resource, (ushort)400),
            (initial.Identity, new DungeonPoint(2, 0), f.Resource, (ushort)400),
            (initial.Identity, new DungeonPoint(0, 0), f.Resource, (ushort)400),
            (initial.Identity, new DungeonPoint(1, 0), f.Resource with { Map = f.Resource.Map with { Id = 99 } }, (ushort)400),
            (initial.Identity, new DungeonPoint(1, 0), f.Resource, (ushort)200),
            (initial.Identity, new DungeonPoint(1, 0), f.Resource with { Monsters = [] }, (ushort)400),
            (initial.Identity, new DungeonPoint(1, 0), f.Resource with { Monsters = [f.Resource.Monsters[0] with { PlacementKind = "[future]" }] }, (ushort)400)
        })
        {
            That(!f.Run.TryPrepareStrongway2019NextRoom(identity, Guid.NewGuid(), target, f.Catalog, resource, key, out _, out _) &&
                SameScene(f.Run.Snapshot, initial) && f.Run.Entry.Maze.MapSpecification![1].Id == 2,
                "旧身份、非下一格、非候选、重复key、空演员或未知生成规则均拒绝且零变更");
        }
        f.KillCurrent(); f.Enter(new(1, 0));
        That(!f.Run.TryPrepareStrongway2019NextRoom(f.Run.Snapshot.Identity, Guid.NewGuid(), new(2, 0), f.Catalog,
            f.Resource, 400, out _, out _), "终点替换语义尚未证实，本入口不改写原终末地图");
    }

    static void BatchPreflight()
    {
        var group = Enumerable.Range(0, 4).Select(i => new Fixture(checked((ushort)(99 + i)))).ToArray();
        var plans = new PreparedStrongway2019RoomReplacement[4];
        for (int i = 0; i < group.Length; i++)
        {
            That(group[i].Prepare(out var prepared), "固定四个原Run分别准备同一队的下一房间资源");
            plans[i] = prepared!;
        }
        var before = group.Select(row => row.Run.Snapshot).ToArray();
        That(group.Select((row, i) => row.Run.CanApplyStrongway2019NextRoom(plans[i])).All(value => value) &&
            group.Select((row, i) => SameScene(row.Run.Snapshot, before[i])).All(value => value),
            "全员预验只读，未提交时四个原Run均不推进身份或地图");
        for (int i = 0; i < group.Length; i++)
            That(group[i].Run.ApplyStrongway2019NextRoom(plans[i]).Outcome == DungeonRunOutcome.Accepted,
                "持全员命令门的调用方在全部预验通过后逐个原子提交");

        var first = new Fixture(99); var second = new Fixture(100);
        bool firstPrepared = first.Prepare(out var one), secondPrepared = second.Prepare(out var two);
        That(firstPrepared && secondPrepared, "准备另一批原实例候选");
        var firstBefore = first.Run.Snapshot;
        second.KillCurrent(); second.Enter(new(1, 0));
        That(first.Run.CanApplyStrongway2019NextRoom(one!) && !second.Run.CanApplyStrongway2019NextRoom(two!) &&
            SameScene(first.Run.Snapshot, firstBefore) && first.Run.Entry.Maze.MapSpecification![1].Id == 2,
            "任一成员身份变化时整批预验失败，尚未提交成员保持原房间和原epoch");
    }

    sealed class Fixture
    {
        public SinglePlayerDungeonRun Run { get; }
        public Authority Authority { get; } = new();
        public Strongway2019Catalog Catalog { get; } = CreateCatalog();
        public DungeonMap Boss { get; } = new("boss", new(2, 0), 3, "map/strongway-boss-fixture.map");
        public DungeonRoomDefinition Resource { get; } = Room(new("map", new(77, 88), 400597, "map/strongway-random-fixture.map"), 109008143);
        public Fixture(ushort character = 99)
        {
            var start = new DungeonMap("map", new(0, 0), 1, "map/strongway-start-fixture.map");
            var middle = new DungeonMap("map", new(1, 0), 2, "map/strongway-middle-fixture.map");
            var maze = new DungeonMaze(0, 3, 1, "fixture", [start.Position], [Boss.Position], [start, middle, Boss], []);
            var entry = new DungeonEntryPlan(character, 1, 5360, 0, byte.MaxValue, 0, maze, start, Boss)
                { DisableFatigueConsumption = true, DisableMonsterAndClearExperience = true };
            var route = new DungeonFixedRoute([DungeonMonsterPlan.FromRoom(Room(start, 11), character, 100),
                DungeonMonsterPlan.FromRoom(Room(middle, 12), character, 200), DungeonMonsterPlan.FromRoom(Room(Boss, 13), character, 300)],
                [new(start.Position, middle.Position), new(middle.Position, Boss.Position)]);
            That(SinglePlayerDungeonRun.TryCreate(entry, 1, 1, route, Authority, out var run, out var reason),
                "真实三房副本路线可建立：" + reason);
            Run = run!;
            That(Run.CompleteLoading(Run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
                "原Run完成真实起始加载");
        }
        public bool Prepare(out PreparedStrongway2019RoomReplacement? prepared) =>
            Run.TryPrepareStrongway2019NextRoom(Run.Snapshot.Identity, Guid.NewGuid(), new(1, 0), Catalog, Resource, 400, out prepared, out _);
        public DungeonRunResult KillCurrent()
        {
            var pending = Run.PrepareConfirmedDeath(Authority.Death(Run.Snapshot.Identity, Run.CurrentMonsterPlan.Monsters.Single().ObjectKey)).Pending;
            That(pending is not null, "正式原Run准备当前房间的真实权威死亡");
            var result = Run.ConfirmCommittedDeath(pending!);
            That(result.Outcome == DungeonRunOutcome.Accepted, "正式原Run提交当前房间真实死亡");
            return result;
        }
        public void Enter(DungeonPoint point)
        {
            That(Run.Move(Run.Snapshot.Identity, Guid.NewGuid(), point).Outcome == DungeonRunOutcome.Accepted,
                "原Run通过已批准连接Move到下一房间");
            That(Run.CompleteLoading(Run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
                "原Run完成下一房间真实加载");
        }
    }
    sealed class Authority : DungeonCombatAuthority
    {
        long sequence;
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key) =>
            ConfirmDeath(identity, Guid.NewGuid(), key, Interlocked.Increment(ref sequence));
    }
    static bool SameScene(DungeonRunSnapshot left, DungeonRunSnapshot right) => left.Identity == right.Identity &&
        left.Phase == right.Phase && left.CurrentMap == right.CurrentMap && left.LivingMonsters.SequenceEqual(right.LivingMonsters);
    static DungeonRoomDefinition Room(DungeonMap map, int monster)
    {
        string[] strings = ["[monster]", "[fixed]", "[boss]", "[/monster]"];
        PvfToken[] tokens = [new(3, 0), new(0, monster), new(0, 0), new(0, 95), new(0, 100),
            new(0, 100), new(0, 0), new(0, 1), new(0, 1), new(6, 1), new(6, 2), new(3, 3)];
        return DungeonRoomCatalog.ParseResource(map, tokens, i => strings[i],
            new Dictionary<int, string> { [monster] = "monster/strongway-fixture.mob" }, new Dictionary<int, string>(), _ => true);
    }
    static Strongway2019Catalog CreateCatalog()
    {
        var tokens = new List<PvfToken>(); var strings = new List<string>();
        Field("dungeon start delay", 5); Field("start area pathgate open delay", 8); Field("return village delay", 6);
        Block("normal dungeon index", [0, 5359, 4]); Block("matching dungeon index", [0, 5360, 4, 1, 5360, 4]);
        Block("reward", [3, 490012737, 1, 1789, 1795]); Field("qualifying dungeon index", 5364);
        Block("random map index", [400596, 400597, 400603]);
        Block("map monster index", [400596, 109008475, 400597, 109008143, 400603, 109008376]);
        Tag("skill system"); Field("add point", 1000, 1); Field("skill waiting time", 1500);
        string[] symbols = ["dplitem3", "dplitem1", "dplitem2", "dplitem0", "dplitem4"];
        int[] points = [0, 30, 45, 60, 100];
        for (int i = 0; i < 5; i++)
        {
            Tag("skill"); Field("button index", i); Tag("type"); Text(i == 4 ? "map change" : "symbol change"); Text(symbols[i]);
            Field("need point", points[i]); Field("global cooltime", 10000); if (i == 0) Field("limit count", 1); Tag("/skill");
        }
        Tag("/skill system"); return new(tokens, i => strings[i]);
        void Tag(string value) { tokens.Add(new(3, strings.Count)); strings.Add("[" + value + "]"); }
        void Text(string value) { tokens.Add(new(6, strings.Count)); strings.Add(value); }
        void Field(string name, params int[] values) { Tag(name); tokens.AddRange(values.Select(x => new PvfToken(0, x))); }
        void Block(string name, int[] values) { Field(name, values); Tag("/" + name); }
    }
}
