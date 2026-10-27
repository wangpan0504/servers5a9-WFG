using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeRaidRevivalTests
{
    // 纯领域只验证配额预留与阶段屏障；本人扣币SQL和正常41入口另行集成验证。
    internal static void RunCore(RaidActor[] actors)
    {
        RunScenario(actors, false);
        RunScenario(actors, true);
    }

    static void RunScenario(RaidActor[] actors, bool terminal)
    {
        int before = Count;
        Bytes(RaidCombatPackets.PhaseCoins(0), [0], "778初始已用为0而非剩余20");
        Bytes(RaidCombatPackets.PhaseCoins(20), [20], "778耗尽为已用20");
        Reject<ArgumentOutOfRangeException>(() => RaidCombatPackets.PhaseCoins(21), "778不能编码越界配额");
        var script = AntonRaidBattleTests.Script(10, 20);
        script = script with { Phases = script.Phases.SetItem(1, script.Phases[1] with
            { Dungeons = script.Phases[1].Dungeons.Add(script.Phases[1].Dungeons[0] with { Id = 21 }) }) };
        var policies = Enumerable.Range(0, 2).Select(phase => new RaidPhaseRewardPolicy("fixture/coins",
            [new(0, 1, [new(100, 1)], [])], new(0, (byte)phase, 1, 0, 0, 0))).ToImmutableArray();
        var battle = new AntonRaidBattle(script, kind: RaidKind.Luke, revival: new("fixture/shared-manual-coins"));
        foreach (var actor in actors) That(battle.Join(actor, policies), "配额领域登记同一当前actor");
        var first = Enter(0, 10, 1, actors.Take(4).ToArray());
        for (int index = 0; index < 8; index++)
        {
            var row = first[index % 4];
            using var token = battle.ReserveLukeRevival(row.Actor, row.Identity, index / 4 + 1)!;
            That(token is not null, "P1共享池前8次按实际死亡事实预留");
            That(battle.ReserveLukeRevival(row.Actor, row.Identity, index / 4 + 1) is null, "同actor不能并发占用两次");
            token!.Commit(); token!.Commit();
        }
        That(battle.ReserveLukeRevival(first[0].Actor, first[0].Identity, 3) is null && battle.View.LukePhaseCoinsUsed is null,
            "四名角色合计8次耗尽，P1不借778伪同步");
        foreach (var row in first) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        var reset = Enter(0, 10, 1, actors.Take(4).ToArray());
        using (var failed = battle.ReserveLukeRevival(reset[0].Actor, reset[0].Identity, 1)!)
            That(failed is not null, "新Run恢复自己的P1额度，旧Run耗尽不阻入图");
        using var pending = battle.ReserveLukeRevival(reset[0].Actor, reset[0].Identity, 1)!;
        That(pending is not null, "未提交撤销可按同死亡事实重试");
        Clear(reset, 10);
        foreach (var row in reset) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        That(battle.HasPendingTransition && battle.View.State == AntonRaidBattleState.Fighting,
            "最后Run离开仍等待已开始的本人扣币事务");
        pending!.Commit(); pending!.Dispose();
        That(pending.Change?.After.State == AntonRaidBattleState.Reward && battle.View.State == AntonRaidBattleState.Reward,
            "原SQL成功确认不因阶段已申请终结而拒绝，产生锁外发布回执");
        foreach (var actor in actors)
            battle.ConfirmReward(actor, new(battle.Instance, 0, 0, 1, actor.Character, "fixture/coins", [new(100, 1)], false, 1)
                { RaidType = RaidKind.Luke });
        That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision) is not null && battle.View.LukePhaseCoinsUsed == 0,
            "新P2安装才初始化全团used0");
        var second = Enter(1, 20, 1, actors.Take(4).ToArray());
        var other = Enter(1, 21, 2, actors.Skip(4).ToArray());
        for (int index = 0; index < 18; index++)
        {
            var row = second[index % 4];
            using var token = battle.ReserveLukeRevival(row.Actor, row.Identity, index / 4 + 1)!;
            That(token is not null, "P2全团配额逐次预留"); token!.Commit();
        }
        using var firstReserved = battle.ReserveLukeRevival(second[0].Actor, second[0].Identity, 6)!;
        using var otherReserved = battle.ReserveLukeRevival(other[0].Actor, other[0].Identity, 1)!;
        That(firstReserved is not null && otherReserved is not null &&
            battle.ReserveLukeRevival(other[1].Actor, other[1].Identity, 1) is null,
            "P2已用18时两队各占最后一格，第三个并发预留不能超额");
        if (!terminal)
        {
            otherReserved!.Commit(); firstReserved!.Commit();
            That(battle.View.LukePhaseCoinsUsed == 20, "两队预留以反序确认精确20，不覆盖对方提交");
            foreach (var row in second.Concat(other)) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
            var again = Enter(1, 20, 2, actors.Skip(4).ToArray());
            That(battle.ReserveLukeRevival(again[0].Actor, again[0].Identity, 1) is null && battle.View.LukePhaseCoinsUsed == 20,
                "全团配额耗尽仍能新Run入图，另一分队不得刷新P2池");
            Console.WriteLine($"Luke raid revival quota: {Count - before} assertions passed");
            return;
        }
        otherReserved!.Commit(); firstReserved!.Dispose();
        That(battle.View.LukePhaseCoinsUsed == 19, "后预留先提交准确19，前预留回滚不消费额度");
        using var last = battle.ReserveLukeRevival(second[0].Actor, second[0].Identity, 6)!;
        That(last is not null && battle.ReserveLukeRevival(other[1].Actor, other[1].Identity, 1) is null,
            "P2已用19时不同队争最后一格仅一预留成功");
        last!.Dispose();
        using var final = battle.ReserveLukeRevival(other[1].Actor, other[1].Identity, 1)!;
        That(final is not null, "最后一格失败撤销后另一队可以按原死亡事实重试");
        Clear(second, 20);
        foreach (var row in second.Concat(other)) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        That(battle.HasPendingTransition && battle.View.LukePhaseCoinsUsed == 19, "P2终结等待最后扣币SQL且保留当前19");
        final!.Commit();
        That(battle.View.LukePhaseCoinsUsed == 20 && final.Change?.After.LukePhaseCoinsUsed == 20 &&
            battle.View.State == AntonRaidBattleState.Reward,
            "P2终结后的预留确认恰为20，旧pending快照不能回退已用计数");
        That(battle.ReserveLukeRevival(second[0].Actor, second[0].Identity, 7) is null,
            "旧Run第21次不能越过终结屏障");
        pending!.Commit(); pending!.Dispose();
        That(battle.View.LukePhaseCoinsUsed == 20, "旧phase迟到token确认/释放不污染新phase");
        Console.WriteLine($"Luke raid revival: {Count - before} assertions passed");

        (RaidActor Actor, DungeonRunIdentity Identity)[] Enter(int phase, uint dungeon, byte squad, RaidActor[] members)
        {
            var run = Guid.NewGuid();
            var rows = members.Select(actor => (Actor: actor, Identity: new DungeonRunIdentity(actor.Character, 0, 1, run, 1))).ToArray();
            using var reservation = battle.ReserveDungeon(new(battle.Instance, phase, squad, RaidKind.Luke), dungeon, rows)!;
            That(reservation is not null, "配额不参与正式入图准入判断"); reservation!.Install();
            return rows;
        }
        void Clear((RaidActor Actor, DungeonRunIdentity Identity)[] rows, uint dungeon) =>
            battle.CompleteLukeDungeon(rows.Select(row => (row.Actor, new DungeonClearFact(row.Identity, dungeon, [Guid.NewGuid()])
                { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/boss.map") })).ToArray());
    }
}
