using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeMemberRouteTests
{
    internal static void RunCore(RaidActor[] actors)
    {
        int before = Count;
        RaidDungeon Dungeon(int id, int route, int capacity = 1) => new(id, RaidDungeonState.Open, $"[hp{id}]", 1, capacity)
            { Declarations = [new("[lockout type]", 0, [new(0, route, null), new(0, 0, null)])] };
        var dungeons = new[] { Dungeon(5311, 4), Dungeon(5313, 5), Dungeon(5063, 0, 2), Dungeon(5066, 0) };
        var script = AntonRaidBattleTests.Script(10, 20);
        script = script with { Symbols = script.Symbols.AddRange(dungeons.Select(row => new KeyValuePair<string, int>(row.HpSymbol, row.Id))).Add("[control]", 999),
            Phases = [script.Phases[0] with { Dungeons = [Dungeon(10, 0)], Rules = [new(
                [new("[on change symbol]", 0, null, "[control]", null, null, [])], [new("[clear phase]", 0, null, null, null, null, [])])] },
                script.Phases[1] with { Dungeons = dungeons.ToImmutableArray(), Rules = [] }] };
        var completion = new LukeRaidCompletionPolicy("fixture/member-lock", 2,
            new(DayOfWeek.Sunday, new TimeOnly(6, 0), TimeSpan.FromHours(8)),
            Enumerable.Range(0, 2).Select(phase => new LukePhaseCompletionPolicy(1, 2,
                new("fixture/member-lock/phase" + phase, [new(0, 1, [new(100, 1)], [])], new(0, (byte)phase, 1, 0, 0, 0)))).ToImmutableArray(),
            LukeParticipationCutoff.TerminalCommittedFacts);
        var battle = new AntonRaidBattle(script, kind: RaidKind.Luke, completion: completion);
        foreach (var actor in actors) That(battle.JoinStart(actor), "个人锁测试登记原八人");
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[control]", 1);
        var candidate = battle.PendingLukeSettlement()!;
        // 无Run的纯领域阶段准备，人工无奖回执不代表SQL或实际阶段通关。
        battle.AcceptLukeSettlement(candidate, new(candidate.Members.Select(member => new RaidPhaseEligibility(candidate.Instance,
            RaidKind.Luke, 0, candidate.Event, candidate.Anchor.ToUnixTimeSeconds(), member.Character, false, false, "fixture/domain-only",
            candidate.Policy.Rewards.Source, candidate.Week, candidate.Policy.Rewards.Offers)).ToImmutableArray()));
        That(battle.View.Phase == 1, "个人锁测试进入独立P2领域状态");
        var light = Bindings(0);
        using (var failed = battle.ReserveDungeon(new(battle.Instance, 1, 1, RaidKind.Luke), 5311, light))
            That(failed is not null, "光路预留可撤销");
        That(actors.All(actor => battle.AllowsLukeMemberRoute(actor, 1, 5313)), "SQL失败前的光路预留不锁任何成员");
        using (var entered = battle.ReserveDungeon(new(battle.Instance, 1, 1, RaidKind.Luke), 5311, light)!) entered.Install();
        foreach (var row in light) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        That(actors.Take(4).All(actor => battle.AllowsLukeMemberRoute(actor, 1, 5313) && battle.AllowsLukeMemberRoute(actor, 1, 5063)),
            "成功进入光路后仍可选择暗路和能源，不按角色锁路线");
        var mixed = Bindings(4); mixed[3] = (actors[0], mixed[3].Identity with { CharacterId = actors[0].Character });
        using (var mixedRoute = battle.ReserveDungeon(new(battle.Instance, 1, 2, RaidKind.Luke), 5313, mixed))
            That(mixedRoute is not null && actors.Skip(4).All(actor => battle.AllowsLukeMemberRoute(actor, 1, 5311)),
                "不同路线经历的角色混队仍可进图，预留撤销不改变资格");
        var dark = Bindings(4);
        using (var entered = battle.ReserveDungeon(new(battle.Instance, 1, 2, RaidKind.Luke), 5313, dark)!) entered.Install();
        foreach (var row in dark) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        var firstEnergy = Bindings(0); var secondEnergy = Bindings(4);
        using (var first = battle.ReserveDungeon(new(battle.Instance, 1, 1, RaidKind.Luke), 5063, firstEnergy)!)
        using (var second = battle.ReserveDungeon(new(battle.Instance, 1, 2, RaidKind.Luke), 5063, secondEnergy))
        {
            That(second is not null, "PARTY MAX=2允许两个分队同时预留同图");
            first.Install(); second!.Install();
        }
        foreach (var row in firstEnergy.Concat(secondEnergy)) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        That(actors.All(actor => battle.AllowsLukeMemberRoute(actor, 1, 5313) && battle.AllowsLukeMemberRoute(actor, 1, 5311)),
            "共享图返回及换队后仍可走两路");
        That(!battle.AllowsLukeMemberRoute(actors[0], 0, 5311) && !battle.AllowsLukeMemberRoute(actors[0], 1, 9999),
            "解除路线锁仍拒绝旧阶段和未登记地图");
        var capped = Bindings(0);
        using (var first = battle.ReserveDungeon(new(battle.Instance, 1, 1, RaidKind.Luke), 5066, capped))
            That(first is not null && battle.ReserveDungeon(new(battle.Instance, 1, 2, RaidKind.Luke), 5066, Bindings(4)) is null,
                "PARTY MAX=1从预留起阻止第二队，不等SQL完成");
        Console.WriteLine($"PASS {Count - before} Luke shared routes and dungeon capacities");

        (RaidActor Actor, DungeonRunIdentity Identity)[] Bindings(int offset)
        {
            var run = Guid.NewGuid();
            return actors.Skip(offset).Take(4).Select(actor => (actor, new DungeonRunIdentity(actor.Character, 0, 1, run, 1))).ToArray();
        }
    }
}
