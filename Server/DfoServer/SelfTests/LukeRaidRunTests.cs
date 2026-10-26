using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeRaidRunTests
{
    // 复用启动测试的八名真实owner；这里隔离Battle预留/状态边界，不代替Runtime入场验收。
    internal static void RunCore(RaidActor[] actors)
    {
        int before = Count;
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
        Console.WriteLine($"PASS {Count - before} 卢克Run预留、阶段屏障和成员事实断言");

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
