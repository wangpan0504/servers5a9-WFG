using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 合成资源只验证显式兼容合同与真实Reserve边界，不冒充原服路线或Runtime资源验收。
internal static class LukeFixedCardRouteTests
{
    internal static void RunCore(RaidActor[] actors)
    {
        int before = Count;
        int[] ids = [5054, 5055, 5056];
        var source = AntonRaidBattleTests.Script(5054);
        var script = source with {
            Symbols = ids.ToImmutableDictionary(id => $"[hp{id}]", id => id).Add("[control]", 9000),
            LukeCardMapRecords = Enumerable.Range(0, 5).Select(n => new LukeCardMapRecord((byte)(n * 3), (byte)(n * 3), (byte)(n * 3))).ToImmutableArray().SetItem(2,new(3,6,9)),
            Phases = [source.Phases[0] with { Dungeons = ids.Select(id => new RaidDungeon(id, RaidDungeonState.Open, $"[hp{id}]", 3, 3)).ToImmutableArray(), Rules = [] }] };
        var maps = new Dictionary<int, string>();
        var rooms = ImmutableDictionary.CreateBuilder<LukeObjectRoom, ImmutableArray<LukeObjectBinding>>();
        var definitions = ids.ToImmutableDictionary(id => id, id => new DungeonDefinition(id, $"dungeon/luke/{id}.dgn", 1, 1,
            Enumerable.Range(0, 15).Select(ordinal => {
                DungeonMap[] specification = [new("map", new(0, 0), id * 100 + ordinal * 2, $"map/{id}/{ordinal}a.map"),
                    new("boss", new(1, 0), id * 100 + ordinal * 2 + 1, $"map/{id}/{ordinal}b.map")];
                foreach (var map in specification) { maps.Add(map.Id, map.Path); rooms.Add(new(id, ordinal, map.Kind, map.Position, map.Id, map.Path), []); }
                return new DungeonMaze(ordinal, 2, 1, "BBEE", [new(0, 0)], [new(1, 0)], specification, []);
            }).ToArray(), []));
        var catalog = new RaidDungeonCatalog(script, definitions, [], maps, _ => true, 1);
        var objects = new LukeRaidObjectCatalog(rooms.ToImmutable(), []);
        var options = new LukeFixedCardRouteOptions("LukeExplicitCardRoutePolicyV1", LukeFixedCardRoute.Fingerprint(catalog), 2,
            [5056, 5054, 5055], [2, 0, 1], 3, LukeRouteFailure.RetrySameStep, LukeRouteReset.ResetStepKeepGroup);
        var policy = LukeFixedCardRoute.Create(options, catalog, objects, out var error)!;
        That(policy is not null && error is null && policy.Choices.Length == 9 && policy.Maze(5054, 0) == 8 && policy.Maze(5056,0)==5 && policy.Maze(5055,0)==11, "显式Fixed组、列绑定与偏移生成九maze");
        foreach (var bad in new[] { options with { Group = 5 }, options with { InitialHp = 2 }, options with { ResourceFingerprint = "wrong" },
            options with { Version = "original" }, options with { ColumnDungeons = [5054, 5054, 5056] }, options with { StepOffsets = [0, 0, 1] } })
            That(LukeFixedCardRoute.Create(bad, catalog, objects, out _) is null, "拒绝隐式或矛盾路线配置");
        foreach (string issue in new[] { "luke-object-action-reference-missing:74171:64252:2", "luke-object-room-unavailable:74171:InvalidDataException" })
            That(LukeFixedCardRoute.Create(options,catalog,new(rooms.ToImmutable(),[issue]),out var diagnostic) is null &&
                diagnostic=="luke-fixed-route-object-issues:"+issue, "所选路线的设施缺口明确拒绝并保留精确诊断，不白名单静默忽略ETC2");
        var absentRoom = rooms.Keys.First(key => key.Dungeon == 5054 && key.MazeOrdinal == 7);
        That(LukeFixedCardRoute.Create(options, catalog, new(rooms.ToImmutable().Remove(absentRoom), []), out _) is null,
            "未选首步但后续maze缺设施覆盖也拒绝整策略");
        var runPolicy = new LukeRaidRunPolicy("fixture/explicit-fixed", ids.Select(id=>new LukeRaidMazeChoice(0,id,0)).ToImmutableArray(),
            LukeRaidRunEconomy.ControlledNoOrdinaryRewards) { FixedRouteOptions = options };
        var startPolicy = new LukeRaidStartPolicy("fixture/fixed",script) { Run = runPolicy };
        var definition = new RaidDefinition(1,"fixture/raid.etc",8,1,1) { Resources = new(script,[],[],[],[],[])
            { EntryCatalog = catalog, LukeObjects = objects } };
        That(runPolicy.Rejection(catalog)=="luke-fixed-route-unresolved", "未装配配置不能绕过启动目录核验");
        var resolved = LukeFixedCardRoute.Resolve(startPolicy,definition)!;
        That(resolved.Run!.FixedRouteOptions is null && resolved.Run.FixedRoute is not null && resolved.Run.Rejection(catalog) is null &&
            LukeFixedCardRoute.ResourceChoices(options,catalog).Count==9, "Runtime解析显式配置后才获得冻结九maze策略");
        That(LukeFixedCardRoute.Resolve(null,definition) is null && ReferenceEquals(LukeFixedCardRoute.Resolve(startPolicy with { Run=null },definition)?.Run,null),
            "无配置不推定Fixed默认策略");
        Reject<InvalidDataException>(()=>LukeFixedCardRoute.Resolve(startPolicy,definition with {Resources=definition.Resources! with {LukeObjects=null}}),
            "Runtime缺设施目录不能静默装配部分路线");
        var inspection = LukeFixedCardRoute.InspectResources(options,catalog,new(rooms.ToImmutable().Remove(absentRoom),[]));
        That(inspection.Length==9 && inspection.Count(row=>!row.Problems.IsEmpty)==1 &&
            inspection.Single(row=>!row.Problems.IsEmpty).MazeOrdinal==7, "九maze诊断不因某步缺覆盖提前停止，也不签发策略");
        var battle = Battle(script);
        var identity = new RaidDungeonIdentity(battle.Instance, 0, 1, RaidKind.Luke);
        var stale = battle.CaptureLukeRoute(0, 5054, policy)!;
        That(battle.ReserveDungeon(identity, 5054, Bindings()) is null, "启用策略后真实Reserve不能省略lease");
        That(battle.ReserveDungeon(identity, 5054, Bindings(), route: stale, mazeOrdinal: 7) is null, "真实Reserve拒绝准备ordinal与lease不符");
        using (var aborted = battle.ReserveDungeon(identity, 5054, Bindings(), route: stale, mazeOrdinal: 8))
            That(aborted is not null, "显式lease允许准备后SQL失败释放预留");
        That(battle.CaptureLukeRoute(0, 5054, policy) == stale, "SQL失败释放不跳路线");
        var failed = Bindings();
        using (var reserve = battle.ReserveDungeon(identity, 5054, failed, route: stale, mazeOrdinal: 8)!) reserve.Install();
        foreach (var row in failed) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        That(battle.CaptureLukeRoute(0, 5054, policy) == stale, "未清图退出保留同一步重试");
        for (int step = 0; step < 3; step++)
        {
            var lease = battle.CaptureLukeRoute(0, 5054, policy)!;
            That(lease.Step == step && lease.MazeOrdinal == new[] { 8, 6, 7 }[step], "每步只消费显式偏移顺序");
            if (step != 0) That(battle.ReserveDungeon(identity, 5054, Bindings(), route: stale, mazeOrdinal: stale.MazeOrdinal) is null,
                "旧ordinal在资源准备后跨清图失效，不能按新代预留");
            var rows = Bindings();
            using (var reserve = battle.ReserveDungeon(identity, 5054, rows, route: lease, mazeOrdinal: lease.MazeOrdinal)!) reserve.Install();
            var facts = Facts(rows);
            int accepted = 0;
            Parallel.For(0, 2, _ => { if (battle.CompleteLukeDungeon(facts) is not null) Interlocked.Increment(ref accepted); });
            That(accepted == 1 && battle.View.Symbols["[hp5054]"] == 2 - step && battle.View.LukeRoutes[5054].Step == step + 1,
                "并发双清同Run只提交一次HP与路线candidate");
            That(battle.CompleteLukeDungeon(facts) is null, "重放不能跳step或重复扣HP");
            foreach (var row in rows) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        }
        That(battle.CaptureLukeRoute(0, 5054, policy) is null && battle.CaptureLukeRoute(0, 5055, policy)?.Step == 0,
            "三清后不再进图且其他副本路线独立");
        var resetScript = script with { Phases = script.Phases.SetItem(0, script.Phases[0] with { Rules = [
            new([new("[on change symbol]", 0, null, "[control]", null, null, []), new("[if]", 0, null, "[control]", null, null, [1])],
                [new("[set dungeon state]", 0, 5054, null, RaidDungeonState.Hide, null, [])]),
            new([new("[on change symbol]", 0, null, "[control]", null, null, []), new("[if]", 0, null, "[control]", null, null, [2])],
                [new("[set dungeon state]", 0, 5054, null, RaidDungeonState.Open, null, [])])] }) };
        var reset = Battle(resetScript);
        var oldLease = reset.CaptureLukeRoute(0, 5054, policy)!;
        var oldRows = Bindings();
        using var pendingInstall = reset.ReserveDungeon(new(reset.Instance, 0, 1, RaidKind.Luke), 5054, oldRows,
            route: oldLease, mazeOrdinal: oldLease.MazeOrdinal)!;
        reset.SetMechanism(reset.Instance, reset.View.Revision, "[control]", 1);
        reset.SetMechanism(reset.Instance, reset.View.Revision, "[control]", 2);
        var fresh = reset.CaptureLukeRoute(0, 5054, policy)!;
        That(fresh.Generation == oldLease.Generation + 1 && fresh.Step == 0 && fresh.Group == oldLease.Group &&
            fresh.Revision > oldLease.Revision && !reset.IsLukeRouteCurrent(oldLease), "重开目标原子重置step并失效旧generation lease，组不变");
        pendingInstall.Install(); // 模拟Reserve成功后SQL提交期间目标重开；安装不得新增扣费后拒绝。
        reset.CompleteLukeDungeon(Facts(oldRows));
        That(reset.CaptureLukeRoute(0, 5054, policy) == fresh && reset.View.Symbols["[hp5054]"] == 3,
            "SQL窗口重开后原预留安装保留旧代，清图只记参与不扣新HP或推进新路线");
        foreach (var row in oldRows) reset.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        That(reset.ReserveDungeon(new(reset.Instance, 0, 1, RaidKind.Luke), 5054, Bindings(), route: oldLease,
            mazeOrdinal: oldLease.MazeOrdinal) is null, "释放占用后旧generation仍不能预留");
        var altered = Battle(script);
        altered.SetMechanism(altered.Instance, altered.View.Revision, "[hp5054]", 2);
        altered.SetMechanism(altered.Instance, altered.View.Revision, "[hp5054]", 3);
        That(altered.CaptureLukeRoute(0, 5054, policy) is null && altered.View.LukeRoutes[5054].Invalid,
            "外部HP变更封闭路线，不用3减HP猜step或恢复");
        var brokenScript = script with { Phases = script.Phases.SetItem(0, script.Phases[0] with { Rules = [new(
            [new("[on change symbol]", 0, null, "[hp5054]", null, null, [])],
            [new("[unknown route behavior]", 0, null, null, null, null, [])])] }) };
        var broken = Battle(brokenScript);
        var brokenRows = Bindings(); var brokenLease = broken.CaptureLukeRoute(0, 5054, policy)!;
        using (var reserve = broken.ReserveDungeon(new(broken.Instance, 0, 1, RaidKind.Luke), 5054, brokenRows,
            route: brokenLease, mazeOrdinal: brokenLease.MazeOrdinal)!) reserve.Install();
        var original = broken.View;
        Reject<InvalidDataException>(() => broken.CompleteLukeDungeon(Facts(brokenRows)), "求值异常明确拒绝");
        That(ReferenceEquals(original, broken.View) && broken.CaptureLukeRoute(0, 5054, policy) == brokenLease &&
            brokenRows.All(row => !broken.HasDungeonContribution(row.Actor, row.Identity.InstanceId)), "异常不发布HP/step也不提前消费清图事实");
        Console.WriteLine($"PASS {Count - before} 显式Fixed兼容路线与lease断言");

        AntonRaidBattle Battle(RaidScript program)
        {
            var result = new AntonRaidBattle(program, kind: RaidKind.Luke, fixedRoute: policy);
            foreach (var actor in actors) That(result.JoinStart(actor), "真实owner登记合成路线Battle");
            return result;
        }
        (RaidActor Actor, DungeonRunIdentity Identity)[] Bindings()
        {
            var run = Guid.NewGuid();
            return actors.Take(4).Select(actor => (actor, new DungeonRunIdentity(actor.Character, 0, 1, run, 1))).ToArray();
        }
        (RaidActor Actor, DungeonClearFact Fact)[] Facts((RaidActor Actor, DungeonRunIdentity Identity)[] rows) => rows.Select(row =>
            (row.Actor, new DungeonClearFact(row.Identity, 5054, [Guid.NewGuid()]) { ClearMap = definitions[5054].Mazes[8].MapSpecification![1] })).ToArray();
    }
}
