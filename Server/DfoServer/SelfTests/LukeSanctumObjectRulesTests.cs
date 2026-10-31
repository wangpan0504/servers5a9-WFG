using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeSanctumObjectRulesTests
{
    internal static void RunCore(RaidActor[] actors)
    {
        int before = Count;
        RaidField Field(string tag, params int[] values) => new(tag, 0, values.Select(value => new RaidValue(0, value, null)).ToImmutableArray());
        var script = AntonRaidBattleTests.Script(5054, 20);
        script = script with { Symbols = script.Symbols.Add("[birth sanctum object]", 117).Add("[extinction sanctum object]", 118)
            .Add("[spread sanctum object]", 119).Add("[energy control open]", 133).Add("[control]", 900),
            Headers = [Field("[birth sanctum]", 5, 5054), Field("[extinction sanctum]", 5, 5055), Field("[spread sanctum]", 5, 5056),
                Field("[1phase obj destroy]", 0, 0, 5, 1, 6, 10, 2, 11, 15)],
            Phases = script.Phases.SetItem(0, script.Phases[0] with { Dungeons = new[] { 5054, 5055, 5056 }
                .Select(id => new RaidDungeon(id, RaidDungeonState.Open, "[hp]", 3, 1)).ToImmutableArray(),
                Rules = [new([new("[on giveup dungeon]", 0, 5054, null, null, null, [])],
                        [new("[reset sanctum object]", 1, 5054, null, null, null, [5054])]),
                    new([new("[on change symbol]", 2, null, "[control]", null, null, [])], [new("[clear phase]", 3, null, null, null, null, [])])] }) };
        var rules = LukeSanctumObjectRules.Create(script);
        for (int total = 0; total <= 15; total++)
        {
            int expected = total <= 5 ? 0 : total <= 10 ? 1 : 2;
            That(rules.Tier(total) == expected, "设施档位全部边界按原闭区间映射");
            // 无在线成员的纯领域阶段准备，不代表开团、资格SQL或自然两阶段通关。
            var phase = new AntonRaidBattle(script, kind: RaidKind.Luke,
                facilities: new("fixture/resource-object-budget", true) { UseResourceBudgets = true });
            phase.SetMechanism(phase.Instance, phase.View.Revision, "[birth sanctum object]", Math.Min(total, 5));
            phase.SetMechanism(phase.Instance, phase.View.Revision, "[extinction sanctum object]", Math.Clamp(total - 5, 0, 5));
            phase.SetMechanism(phase.Instance, phase.View.Revision, "[spread sanctum object]", Math.Max(total - 10, 0));
            phase.SetMechanism(phase.Instance, phase.View.Revision, "[control]", 1);
            That(phase.AdvanceAfterRewards(phase.Instance, phase.View.Revision) is not null && phase.View.Phase == 1 &&
                phase.View.Symbols["[energy control open]"] == expected, "设施原计数跨阶段冻结为能源档位");
        }
        foreach (var invalid in new[] { script.Headers.SetItem(0, Field("[birth sanctum]", 0, 5054)),
            script.Headers.SetItem(1, Field("[extinction sanctum]", 5, 5054)),
            script.Headers.SetItem(3, Field("[1phase obj destroy]", 0, 0, 5, 1, 7, 10, 2, 11, 15)),
            script.Headers.SetItem(3, Field("[1phase obj destroy]", 0, 0, 5, 1, 5, 10, 2, 11, 15)),
            script.Headers.SetItem(3, Field("[1phase obj destroy]", 0, 0, 5, 1, 6, 10, 2, 11, 14)) })
            Reject<InvalidDataException>(() => LukeSanctumObjectRules.Create(script with { Headers = invalid }), "拒绝错误预算归属、档位缺口重叠及覆盖不足");

        var map = new DungeonMap("map", new(0, 0), 77, "fixture/budget.map");
        var next = map with { Position = new(1, 0) };
        var maze = new DungeonMaze(0, 2, 1, "", [map.Position], [next.Position], [map, next], []);
        LukeObjectRoom Key(DungeonMap room) => new(5054, 0, room.Kind, room.Position, room.Id, room.Path);
        LukeObjectBinding Binding(DungeonMap room) => new(Key(room), 0, 64250, 117, 0,
            "passiveobject/actionobject/map/luke_raid/phase1/sanctum_of_birth/action/luke_mainmchine_destroy_mistral_raid.act");
        var objects = new LukeRaidObjectCatalog(new Dictionary<LukeObjectRoom, ImmutableArray<LukeObjectBinding>>
            { [Key(map)] = [Binding(map)], [Key(next)] = [Binding(next)] }.ToImmutableDictionary(), []);
        var battle = new AntonRaidBattle(script, kind: RaidKind.Luke,
            facilities: new("fixture/resource-object-budget", true) { UseResourceBudgets = true });
        foreach (var actor in actors) That(battle.JoinStart(actor), "设施预算测试登记当前八人");
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[birth sanctum object]", 4);
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[extinction sanctum object]", 2);
        var run = Guid.NewGuid();
        var rows = actors.Take(4).Select(actor => (Actor: actor, Identity: new DungeonRunIdentity(actor.Character, 0, 1, run, 1))).ToArray();
        using (var reserve = battle.ReserveDungeon(new(battle.Instance, 0, 1, RaidKind.Luke), 5054, rows,
            new LukeRaidObjectRegistration(run, 5054, maze, objects))!) reserve.Install();
        battle.ObserveLukeRun(run, rows, map.Id, map);
        var snapshot = new DungeonRunSnapshot(rows[0].Identity, SinglePlayerDungeonPhase.Dungeon, map, []);
        That(battle.ConfirmLukeFacility(actors[0], snapshot, 117, 1, 1).Outcome == LukeFacilityReportOutcome.Applied &&
            battle.View.Symbols["[birth sanctum object]"] == 5, "米斯特尔设施精确动作计入剩余一份预算");
        var nextRows = rows.Select(row => (row.Actor, Identity: row.Identity with { Epoch = 2 })).ToArray();
        battle.ObserveLukeRun(run, nextRows, next.Id, next);
        That(battle.ConfirmLukeFacility(actors[0], snapshot with { Identity = nextRows[0].Identity, CurrentMap = next }, 117, 1, 1).Outcome ==
            LukeFacilityReportOutcome.Duplicate && battle.View.Symbols["[birth sanctum object]"] == 5, "不同有效设施超过资源cap也不重复累计");
        foreach (var row in rows) battle.ReleaseDungeon(row.Actor, run);
        That(battle.View.Symbols["[birth sanctum object]"] == 0 && battle.View.Symbols["[extinction sanctum object]"] == 2 &&
            battle.View.Deferred.Values.All(action => action.Tag != "[reset sanctum object]"),
            "原GiveUp reset清所属圣所全部累计，不只撤回本Run增量，不影响别圣所");
        Console.WriteLine($"PASS {Count - before} Luke sanctum budgets reset and phase tier inheritance");
    }
}
