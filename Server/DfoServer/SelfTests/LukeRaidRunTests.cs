using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeRaidRunTests
{
    // 复用启动测试的八名真实owner；这里隔离Battle预留/状态边界，不代替Runtime入场验收。
    internal static void RunCore(RaidActor[] actors)
    {
        int before = Count;
        Bytes(RaidCombatPackets.Symbols([(67, 3), (140, -1)]),
            [2, 67, 0, 0, 0, 3, 0, 0, 0, 140, 0, 0, 0, 255, 255, 255, 255], "583显式symbol ID及完整有符号位型");
        Bytes(RaidCombatPackets.DungeonState(5055, 0, RaidDungeonState.Open), [191, 19, 0, 0, 0, 0], "586完整6B保留中间字节");
        Reject<InvalidDataException>(() => RaidCombatPackets.Symbols([(0, 0)]), "快照不能注入共享清表哨兵");
        var script = AntonRaidBattleTests.Script(10, 20);
        script = script with { Phases = script.Phases.SetItem(0, script.Phases[0] with
            { Dungeons = script.Phases[0].Dungeons.Add(new(11, RaidDungeonState.Open, "[hp]", 1, 1)) }) };
        var map = new DungeonMap("boss", new(0, 0), 77, "fixture/boss.map");
        foreach (bool secondCommits in new[] { false, true })
        {
            var battle = Battle(script);
            var first = Bindings(0); var second = Bindings(4);
            var identity = new RaidDungeonIdentity(battle.Instance, 0, 1, RaidKind.Luke);
            using var reservation = battle.ReserveDungeon(identity, 10, first)!;
            That(reservation is not null, "原Battle接受有界第一分队预留");
            That(battle.ReserveDungeon(identity with { Squad = 2 }, 10, second) is null, "另一分队不能预留已占同图");
            using var other = battle.ReserveDungeon(identity with { Squad = 2 }, 11, second)!;
            That(other is not null, "另一图允许另一分队独立预留");
            reservation!.Install(); reservation.Dispose();
            var facts = first.Select(row => (row.Actor, Fact: new DungeonClearFact(row.Identity, 10, [Guid.NewGuid()]) { ClearMap = map })).ToArray();
            That(battle.CompleteLukeDungeon(facts[..3]) is null && !battle.HasPendingTransition &&
                first.All(row => !battle.HasDungeonContribution(row.Actor, row.Identity.InstanceId)), "不完整角色事实不记录也不推进");
            var invalid = facts.ToArray(); invalid[3] = (invalid[3].Actor, invalid[3].Fact with { Identity = invalid[3].Fact.Identity with { TaskAttempt = 9 } });
            That(battle.CompleteLukeDungeon(invalid) is null && !battle.HasPendingTransition, "末成员身份错误整批零事实");
            That(battle.CompleteLukeDungeon(facts) is not null && battle.HasPendingTransition &&
                battle.View is { State: AntonRaidBattleState.Fighting, Phase: 0 } && first.All(row => battle.HasDungeonContribution(row.Actor, row.Identity.InstanceId)),
                "已正式提交全队清图保留四份参与；入场预留/旧Run存在时不提交Reward转换");
            That(battle.CompleteLukeDungeon(facts) is null, "同Run重放不重复推进或参与");
            var clears = battle.RosterClears(battle.View)!;
            That(first.All(row => clears.GetValueOrDefault(row.Actor.Character) == 1) &&
                second.All(row => clears.GetValueOrDefault(row.Actor.Character) == 0),
                "卢克通关列只展示已提交成员事实，未清图另一队保持零，重放不重复计数");
            That(battle.ReserveDungeon(identity, 11, first) is null, "pending拒绝新的入场预留");
            if (secondCommits)
            {
                other!.Install();
                var late = second.Select(row => (row.Actor, Fact: new DungeonClearFact(row.Identity, 11, [Guid.NewGuid()]) { ClearMap = map })).ToArray();
                That(battle.CompleteLukeDungeon(late) is not null && battle.HasPendingTransition &&
                    second.All(row => battle.HasDungeonContribution(row.Actor, row.Identity.InstanceId)),
                    "逻辑阶段终结后第二队已提交清图仍接纳精确参与，不重开旧阶段业务");
            }
            foreach (var row in first) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
            That(battle.HasPendingTransition, "最后预留释放前保留已提交事实和待转换");
            other!.Dispose(); other.Dispose();
            That(secondCommits == battle.HasPendingTransition, "第二队SQL失败释放预留或成功保活跃Run具有不同退场条件");
            if (secondCommits)
            {
                That(battle.View.State == AntonRaidBattleState.Fighting, "新Run成功安装后仍必须原事务退出才能提交候选");
                foreach (var row in second) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
            }
            That(!battle.HasPendingTransition && battle.View is { State: AntonRaidBattleState.Reward, Phase: 0, Revision: 1 },
                "最后旧Run释放后仅一次Fighting到Reward，不直接跳phase");
            That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision) is null, "未装配Luke奖励合同不得绕过阶段领奖或索引空策略");
            That(first.All(row => battle.HasDungeonContribution(row.Actor, row.Identity.InstanceId)), "释放占用不丢已提交的当前成员参与事实");
        }
        var quiet = script with { Phases = script.Phases.Select(phase => phase with { Rules = [] }).ToImmutableArray() };
        var retained = Battle(quiet);
        var original = Bindings(0);
        var admission = new RaidDungeonIdentity(retained.Instance, 0, 1, RaidKind.Luke);
        using (var reserve = retained.ReserveDungeon(admission, 10, original)!) reserve.Install();
        foreach (var row in original) retained.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        var replacement = Bindings(0);
        using (var reserve = retained.ReserveDungeon(admission, 10, replacement)!) reserve.Install();
        foreach (var row in original) retained.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        That(retained.ReserveDungeon(admission with { Squad = 2 }, 10, Bindings(4)) is null, "旧Run迟到释放不触新Run占用");
        foreach (var row in replacement) retained.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        using var freed = retained.ReserveDungeon(admission with { Squad = 2 }, 10, Bindings(4));
        That(freed is not null, "本Run全员真实释放后图可再次预留");
        var departed = Battle(quiet);
        var group = Bindings(0);
        using (var reserve = departed.ReserveDungeon(new(departed.Instance, 0, 1, RaidKind.Luke), 10, group)!) reserve.Install();
        departed.ReleaseDungeon(group[0].Actor, group[0].Identity.InstanceId);
        var survivors = group.Skip(1).Select(row => (row.Actor, Fact: new DungeonClearFact(row.Identity, 10, [Guid.NewGuid()]) { ClearMap = map })).ToArray();
        That(departed.CompleteLukeDungeon(survivors) is not null &&
            !departed.HasDungeonContribution(group[0].Actor, group[0].Identity.InstanceId) &&
            survivors.All(row => departed.HasDungeonContribution(row.Actor, row.Fact.Identity.InstanceId)),
            "已离开Run但未提交清图的成员不因共享Cleared获得参与资格");
        ObjectiveGenerations();
        LukeFixedCardRouteTests.RunCore(actors);
        LukeMemberRouteTests.RunCore(actors);
        LukeEnergyObjectTests.RunCore(actors);
        LukeSanctumObjectRulesTests.RunCore(actors);
        Console.WriteLine($"PASS {Count - before} 卢克Run预留、阶段屏障和成员事实断言");

        void ObjectiveGenerations()
        {
            RaidClause Change(string key) => new("[on change symbol]", 0, null, key, null, null, []);
            RaidClause If(int value) => new("[if]", 0, null, "[control]", null, null, [value]);
            RaidClause Set(string key, int value, bool add = false) => new("[set]", 0, null, key, null, null, [value])
                { Operator = add ? "[+]" : null };
            RaidClause State(RaidDungeonState state) => new("[set dungeon state]", 0, 10, null, state, null, []);
            var timer = new RaidTimer(1, 10);
            var symbols = quiet.Symbols.Add("[control]", 8).Add("[entered]", 9).Add("[maps]", 10).Add("[clears]", 11);
            var program = quiet with { Symbols = symbols, Phases = quiet.Phases.SetItem(0, quiet.Phases[0] with
            {
                Dungeons = [new(10, RaidDungeonState.Open, "[hp]", 3, 4)],
                Rules = [
                    new([new("[on enter dungeon]", 0, 10, null, null, null, [])], [Set("[entered]", 1, true)]),
                    new([new("[on enter map]", 0, null, null, null, null, []) { MapId = 77 }], [Set("[maps]", 1, true)]),
                    new([Change("[control]"), If(1)], [State(RaidDungeonState.Hide)]),
                    new([Change("[control]"), If(2)], [State(RaidDungeonState.Open)]),
                    new([Change("[control]"), If(3)], [State(RaidDungeonState.Open)]),
                    new([Change("[control]"), If(4)], [new("[set timer]", 0, null, null, null, timer, [1, 10, 10])]),
                    new([Change("[control]"), If(5)], [new("[reset timer]", 0, null, null, null, timer, [1, 10])]),
                    new([new("[on change dungeon state]", 0, 10, null, null, null, []),
                        new("[check dungeon state]", 0, 10, null, RaidDungeonState.Clear, null, [])], [Set("[clears]", 1, true)])]
            }) };
            var state = Battle(program);
            for (int pass = 1; pass <= 3; pass++)
            {
                var rows = Bindings(0);
                using (var reservation = state.ReserveDungeon(new(state.Instance, 0, 1, RaidKind.Luke), 10, rows)!) reservation.Install();
                state.ObserveLukeRun(rows[0].Identity.InstanceId, rows);
                state.ObserveLukeRun(rows[0].Identity.InstanceId, rows, 77);
                long revision = state.View.Revision;
                That(state.ObserveLukeRun(rows[0].Identity.InstanceId, rows, 77) is null && state.View.Revision == revision &&
                    state.View.Symbols["[entered]"] == pass && state.View.Symbols["[maps]"] == pass,
                    "正式Enter与Map每Run/epoch一次，重复同步不重复算术");
                if (pass == 3)
                {
                    var nextRoom = rows.Select(row => (row.Actor, Identity: row.Identity with { Epoch = row.Identity.Epoch + 1 })).ToArray();
                    state.ObserveLukeRun(rows[0].Identity.InstanceId, nextRoom, 77);
                    long roomRevision = state.View.Revision;
                    state.ObserveLukeRun(rows[0].Identity.InstanceId, nextRoom.Reverse().ToArray(), 77);
                    That(state.View.Symbols["[maps]"] == pass + 1 && state.View.Revision == roomRevision,
                        "同Run下一权威房间epoch可再次触发，换回调队长/重复全员回调不再触发");
                }
                var facts = rows.Select(row => (row.Actor, Fact: new DungeonClearFact(
                    pass == 3 ? row.Identity with { Epoch = row.Identity.Epoch + 1 } : row.Identity,
                    10, [Guid.NewGuid()]) { ClearMap = map })).ToArray();
                state.CompleteLukeDungeon(facts);
                That(state.View.Symbols["[hp]"] == 3 - pass && state.View.Symbols["[clears]"] == (pass == 3 ? 1 : 0) &&
                    state.View.Dungeons[10] == (pass == 3 ? RaidDungeonState.Clear : RaidDungeonState.Open),
                    "HP3按不同共享Run扣1，四份角色事实不扣4，第三次才触发Clear");
                revision = state.View.Revision;
                That(state.CompleteLukeDungeon(facts) is null && state.View.Revision == revision, "清图重放不重复HP或加法");
                if (pass == 1)
                {
                    Control(3);
                    That(state.View.Symbols["[hp]"] == 2 && state.View.DungeonGenerations[10] == 1,
                        "重复Open不恢复剩余HP或续目标代");
                }
                foreach (var row in rows) state.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
            }
            Control(2);
            That(state.View.Symbols["[hp]"] == 3 && state.View.DungeonGenerations[10] == 2, "Clear重新Open恢复资源HP且推进目标代");
            var old = Bindings(0);
            using (var reservation = state.ReserveDungeon(new(state.Instance, 0, 1, RaidKind.Luke), 10, old)!) reservation.Install();
            Control(1);
            var hidden = state.View;
            state.ObserveLukeRun(old[0].Identity.InstanceId, old, 77);
            var stale = old.Select(row => (row.Actor, Fact: new DungeonClearFact(row.Identity, 10, [Guid.NewGuid()]) { ClearMap = map })).ToArray();
            That(state.CompleteLukeDungeon(stale) is not null && ReferenceEquals(hidden, state.View) &&
                old.All(row => state.HasDungeonContribution(row.Actor, row.Identity.InstanceId)),
                "Hide后旧Run只能记录已提交参与，不能改回Clear或执行Enter/Map");
            Control(2);
            That(state.View.DungeonGenerations[10] == 3 && state.View.Symbols["[hp]"] == 3 &&
                state.ReserveDungeon(new(state.Instance, 0, 2, RaidKind.Luke), 10, Bindings(4)) is null,
                "重开不能绕旧Run占用制造第三个分队实例");
            foreach (var row in old) state.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
            Control(4);
            long generation = state.View.TimerGenerations[timer];
            Control(5); Control(4);
            That(state.ExpireLukeTimer(state.Instance, timer, generation) is null && state.View.Timers.ContainsKey(timer),
                "RESET后重设timer的新generation不被旧deadline取消");
            That(state.ExpireLukeTimer(state.Instance, timer, state.View.TimerGenerations[timer]) is not null && !state.View.Timers.ContainsKey(timer),
                "当前timer代恰好消费一次");
            Control(5);
            That(!state.View.Timers.ContainsKey(timer), "未设置timer的RESET幂等取消");

            var overflow = Battle(program);
            overflow.SetMechanism(overflow.Instance, overflow.View.Revision, "[entered]", int.MaxValue);
            var overflowActors = Bindings(0);
            using (var reservation = overflow.ReserveDungeon(new(overflow.Instance, 0, 1, RaidKind.Luke), 10, overflowActors)!) reservation.Install();
            var originalView = overflow.View;
            Reject<OverflowException>(() => overflow.ObserveLukeRun(overflowActors[0].Identity.InstanceId, overflowActors),
                "加法溢出明确拒绝而非整数环回");
            That(ReferenceEquals(overflow.View, originalView), "失败求值不发布部分符号结果");
            overflow.SetMechanism(overflow.Instance, overflow.View.Revision, "[entered]", 0);
            overflow.ObserveLukeRun(overflowActors[0].Identity.InstanceId, overflowActors);
            That(overflow.View.Symbols["[entered]"] == 1, "求值失败未提前消费Run事件，原实例可恢复");

            var terminalProgram = program with { Phases = program.Phases.SetItem(0, program.Phases[0] with
            { Dungeons = [new(10, RaidDungeonState.Open, "[hp]", 1, 4)],
                Rules = program.Phases[0].Rules.Add(new(
                    [new("[on change dungeon state]", 80, 10, null, null, null, []),
                     new("[check dungeon state]", 81, 10, null, RaidDungeonState.Clear, null, [])],
                    [new("[clear phase]", 82, null, null, null, null, [])])) }) };
            var terminal = Battle(terminalProgram);
            terminal.SetMechanism(terminal.Instance, terminal.View.Revision, "[control]", 4);
            long expiredGeneration = terminal.View.TimerGenerations[timer];
            var ending = Bindings(0);
            using (var reservation = terminal.ReserveDungeon(new(terminal.Instance, 0, 1, RaidKind.Luke), 10, ending)!) reservation.Install();
            terminal.CompleteLukeDungeon(ending.Select(row => (row.Actor,
                Fact: new DungeonClearFact(row.Identity, 10, [Guid.NewGuid()]) { ClearMap = map })).ToArray());
            That(terminal.HasPendingTransition && terminal.View.State == AntonRaidBattleState.Fighting &&
                terminal.View.Timers.IsEmpty && terminal.View.TimerGenerations.IsEmpty &&
                terminal.ExpireLukeTimer(terminal.Instance, timer, expiredGeneration) is null,
                "逻辑ClearPhase候选形成即停止旧timer，不等四名成员全部回城");
            foreach (var row in ending) terminal.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
            That(terminal.View.State == AntonRaidBattleState.Reward && terminal.View.Timers.IsEmpty,
                "旧Run退出只提交Reward，不恢复已停止timer");

            var phaseProgram = program with { Phases = program.Phases
                .SetItem(0, program.Phases[0] with { Rules = program.Phases[0].Rules.Add(new(
                    [Change("[control]"), If(9)], [new("[clear phase]", 83, null, null, null, null, [])])) })
                .SetItem(1, program.Phases[1] with { Rules = [new([], [new("[set timer]", 84, null, null, null, timer, [1, 10, 10])])] }) };
            // 无在线actor的纯解释器阶段测试，不代替真实Runtime领奖。
            var phases = new AntonRaidBattle(phaseProgram, kind: RaidKind.Luke);
            phases.SetMechanism(phases.Instance, phases.View.Revision, "[entered]", 77);
            phases.SetMechanism(phases.Instance, phases.View.Revision, "[control]", 4);
            long oldPhaseTimer = phases.View.TimerGenerations[timer];
            phases.SetMechanism(phases.Instance, phases.View.Revision, "[control]", 9);
            phases.AdvanceAfterRewards(phases.Instance, phases.View.Revision);
            That(phases.View.Phase == 1 && phases.View.Symbols["[entered]"] == 77 &&
                phases.View.TimerGenerations[timer] > oldPhaseTimer &&
                phases.ExpireLukeTimer(phases.Instance, timer, oldPhaseTimer) is null,
                "symbol跨phase保存，timer键可引用其它phase的DGN且generation不回退");
            That(phases.DungeonSnapshot(phases.View).Single(row=>row.Id==10).State==RaidDungeonState.Hide &&
                phases.DungeonSnapshot(phases.View).Single(row=>row.Id==20).State==RaidDungeonState.Open,
                "新phase快照显式隐藏上一phase图，完整重同步不留旧Open");

            var departureProgram = program with { Phases = program.Phases.SetItem(0, program.Phases[0] with
            { Rules = program.Phases[0].Rules.Add(new(
                [new("[on giveup dungeon]", 90, 10, null, null, null, [])],
                [Set("[maps]", 1, true), new("[reset sanctum object]", 91, 10, null, null, null, [10])]))
                .Add(new([new("[phase end]", 92, null, null, null, null, [0])],
                    [new("[fail phase]", 93, null, null, null, null, [])])) }) };
            var departing = Battle(departureProgram);
            var leaving = Bindings(0);
            using (var reservation = departing.ReserveDungeon(new(departing.Instance, 0, 1, RaidKind.Luke), 10, leaving)!) reservation.Install();
            foreach (var row in leaving.Take(3)) departing.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
            That(departing.View.Deferred.IsEmpty && departing.View.Symbols["[maps]"] == 0,
                "部分成员离开不是整Run放弃，不提前重置对象");
            departing.ReleaseDungeon(leaving[3].Actor, leaving[3].Identity.InstanceId);
            var abandoned = departing.View;
            foreach (var row in leaving) departing.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
            That(ReferenceEquals(abandoned, departing.View) && abandoned.Symbols["[maps]"] == 1 &&
                abandoned.Deferred[91].Tag == "[reset sanctum object]",
                "最后成员正式放弃只接纳一次，未知对象行为有界保留而非假执行");
            That(departing.CompleteDeferred(departing.Instance, abandoned.Revision, 91, 10) is null &&
                ReferenceEquals(abandoned, departing.View), "对象重置不能误入随机孵化场完成分支");
            That(departing.EndLukePhase(departing.Instance, 1) is null, "非当前phase的deadline拒绝");
            departing.EndLukePhase(departing.Instance, 0);
            var failed = departing.View;
            That(failed.PhaseExpired && failed.Deferred.IsEmpty && failed.Timers.IsEmpty &&
                failed.Dungeons.Values.All(value=>value==RaidDungeonState.Hide) &&
                departing.CompleteDeferred(departing.Instance, failed.Revision, 93, 10) is null &&
                departing.EndLukePhase(departing.Instance, 0) is null &&
                departing.ReserveDungeon(new(departing.Instance, 0, 1, RaidKind.Luke), 10, Bindings(0)) is null,
                "资源FAIL提交原失败退场事实并封图清计时，不发猜测失败包、不允许重新入图或重复终结");

            void Control(int value) => state.SetMechanism(state.Instance, state.View.Revision, "[control]", value);
        }

        AntonRaidBattle Battle(RaidScript definition)
        {
            var value = new AntonRaidBattle(definition, kind: RaidKind.Luke);
            foreach (var actor in actors) That(value.JoinStart(actor), "同一Battle登记当前八名actor");
            return value;
        }
        (RaidActor Actor, DungeonRunIdentity Identity)[] Bindings(int first)
        {
            var instance = Guid.NewGuid();
            return actors.Skip(first).Take(4).Select(actor => (actor, new DungeonRunIdentity(actor.Character, 0, 1, instance, 1))).ToArray();
        }
    }
}
