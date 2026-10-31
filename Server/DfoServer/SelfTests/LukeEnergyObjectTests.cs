using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeEnergyObjectTests
{
    internal static void RunCore(RaidActor[] actors)
    {
        int before = Count;
        foreach (int dungeon in new[] { 5063, 5066 }) Verify(dungeon);
        Console.WriteLine($"PASS {Count - before} Luke energy object source and shared clear assertions");

        void Verify(int dungeon)
        {
            int template = dungeon == 5063 ? 64062 : 64060, symbol = dungeon == 5063 ? 113 : 114;
            string side = dungeon == 5063 ? "light" : "dark";
            var map = new DungeonMap("boss", new(0, 0), dungeon == 5063 ? 74232 : 74247, "map/fixture-energy.map");
            var maze = new DungeonMaze(0, 1, 1, "BB", [map.Position], [map.Position], [map], [])
                { ClearConditions = [new("destroy object", template, 1)] };
            var key = new LukeObjectRoom(dungeon, 0, map.Kind, map.Position, map.Id, map.Path);
            var binding = new LukeObjectBinding(key, 0, template, symbol, 0,
                "passiveobject/actionobject/map/luke_raid/phase2/link_obj/action/solium_" + side + "_destroy.act");
            var objects = new LukeRaidObjectCatalog(ImmutableDictionary<LukeObjectRoom, ImmutableArray<LukeObjectBinding>>.Empty
                .Add(key, [binding]), []);
            That(objects.Energy(dungeon, maze) == binding && objects.Energy(dungeon,
                maze with { ClearConditions = [new("destroy object", template + 1, 1)] }) is null,
                "能源能力绑定精确迷宫终点及原清图目标");
            var script = AntonRaidBattleTests.Script(10, 20);
            script = script with { Symbols = script.Symbols.Add("[control]", 900).Add("[hp5063]", 901).Add("[hp5066]", 902),
                Phases = [script.Phases[0] with { Rules = [new([new("[on change symbol]", 0, null, "[control]", null, null, [])],
                    [new("[clear phase]", 0, null, null, null, null, [])])] }, script.Phases[1] with {
                    Dungeons = new[] { 5063, 5066 }.Select(id => new RaidDungeon(id, RaidDungeonState.Open, $"[hp{id}]", 1, 1)).ToImmutableArray(),
                    Rules = [new([new("[on change symbol]", 0, null, "[control]", null, null, []),
                        new("[if]", 0, null, "[control]", null, null, [2])],
                        [new("[set dungeon state]", 0, dungeon, null, RaidDungeonState.Open, null, [])])] }] };
            var completion = new LukeRaidCompletionPolicy("fixture/energy", 2,
                new(DayOfWeek.Sunday, new TimeOnly(6, 0), TimeSpan.FromHours(8)),
                Enumerable.Range(0, 2).Select(phase => new LukePhaseCompletionPolicy(1, 2,
                    new("fixture/energy/phase" + phase, [new(0, 1, [new(100, 1)], [])], new(0, (byte)phase, 1, 0, 0, 0)))).ToImmutableArray(),
                LukeParticipationCutoff.TerminalCommittedFacts);
            var battle = new AntonRaidBattle(script, kind: RaidKind.Luke, completion: completion, lukeEnergyObjects: true);
            foreach (var actor in actors) That(battle.JoinStart(actor), "能源测试登记原八人owner");
            battle.SetMechanism(battle.Instance, battle.View.Revision, "[control]", 1);
            var settlement = battle.PendingLukeSettlement()!;
            // 仅准备纯领域P2；无奖人工回执不冒充SQL或自然P1通关。
            battle.AcceptLukeSettlement(settlement, new(settlement.Members.Select(member => new RaidPhaseEligibility(settlement.Instance,
                RaidKind.Luke, 0, settlement.Event, settlement.Anchor.ToUnixTimeSeconds(), member.Character, false, false, "fixture/domain-only",
                settlement.Policy.Rewards.Source, settlement.Week, settlement.Policy.Rewards.Offers)).ToImmutableArray()));
            That(battle.View.Phase == 1 && battle.SymbolSnapshot(battle.View).Count(row => row.Id is 113 or 114 && row.Value == 0) == 2,
                "P2原值能源信号初始化为0，不补写etc命名表");
            var instance = Guid.NewGuid();
            var projections = actors.Take(4).Select(actor =>
            {
                var character = actor.Session.Selected! with { ActiveMainQuests = [] };
                var entry = new DungeonEntryPlan(actor.Character, character.Revision, (uint)dungeon, 0, byte.MaxValue, 0, maze, map, map)
                    { RaidEntry = new(battle.Instance, 1, 1, RaidKind.Luke), LukeEnergyObject = binding };
                var room = new DungeonRoomDefinition(map, [], [new(template, [template, 0, 0, 0], "fixture-energy.obj")], []);
                var route = new DungeonFixedRoute([DungeonMonsterPlan.FromRoom(room, actor.Character, 100)], [])
                    { PassiveClearTarget = template };
                That(SinglePlayerDungeonRun.TryCreate(entry, 1, 1, route, new Authority(), out var run, out _), "能源投影沿真实Run建立");
                run!.BindPartyInstance(instance);
                run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
                var source = new QuestSceneEvent(Guid.NewGuid(), run.Snapshot.Identity, dungeon, map.Id, template, 0, new string('a', 64));
                var prepared = run.PreparePassiveClear(source.Identity, source.OperationId, template, 0)!;
                var plan = QuestLifecyclePlan.ObservePassive(character, new QuestCatalog([]), entry, source, sceneClear: prepared, run: run);
                That(plan is { IsPartySceneOnly: true, QuestId: 0 } && entry.PartyRoute is null &&
                    prepared.Result.ClearFact is { ConfirmedDeathEvents.Count: 0, PassiveSource: not null },
                    "无任务队长允许真实物件持久候选，保留空怪物死亡集");
                return (Actor: actor, Run: run, Prepared: prepared);
            }).ToArray();
            var rows = projections.Select(row => (row.Actor, row.Run.Snapshot.Identity)).ToArray();
            var identity = new RaidDungeonIdentity(battle.Instance, 1, 1, RaidKind.Luke);
            That(battle.ReserveDungeon(identity, (uint)dungeon, rows, mazeOrdinal: 0) is null,
                "能源Run缺精确对象登记不能预留");
            var registration = new LukeRaidObjectRegistration(instance, dungeon, maze, objects);
            using (var reservation = battle.ReserveDungeon(identity, (uint)dungeon, rows, registration, mazeOrdinal: 0)!) reservation.Install();
            var facts = projections.Select(row => (row.Actor, Fact: row.Prepared.Result.ClearFact!)).ToArray();
            That(battle.CompleteLukeDungeon(facts) is null, "全员加载房间尚未登记不能消费能源事实");
            battle.ObserveLukeRun(instance, rows, map.Id, map);
            var original = facts[0].Fact;
            var passive = original.PassiveSource!;
            foreach (var invalid in new[] {
                original with { PassiveSource = null },
                original with { PassiveSource = passive with { TemplateId = template + 1 } },
                original with { PassiveSource = passive with { SourceOrdinal = 1 } },
                original with { PassiveSource = passive with { OperationId = Guid.Empty } },
                original with { PassiveSource = passive with { Identity = passive.Identity with { Epoch = passive.Identity.Epoch + 1 } } },
                original with { ClearMap = map with { Position = new(1, 0) } } })
                That(battle.CompleteLukeDungeon([(facts[0].Actor, invalid), .. facts.Skip(1)]) is null,
                    "错误能源来源整批拒绝，不扣HP或发布信号");
            That(battle.CompleteLukeDungeon(facts[..3]) is null && battle.View.LukeEnergySignals[symbol] == 0,
                "缺一人提交不推进图或信号");
            foreach (var row in projections) row.Run.ConfirmCommittedPassiveClear(row.Prepared);
            That(battle.CompleteLukeDungeon(facts) is not null && battle.CompleteLukeDungeon(facts) is null &&
                battle.View.LukeEnergySignals[symbol] == 1 && battle.View.Symbols[$"[hp{dungeon}]"] == 0,
                "四份正式物件事实只推进一次Run并发布原值113/114");
            battle.SetMechanism(battle.Instance, battle.View.Revision, "[control]", 2);
            That(battle.View.LukeEnergySignals[symbol] == 0 && battle.CompleteLukeDungeon(facts) is null,
                "目标重开清除旧能源信号，旧Run不能再次触发");
            foreach (var row in rows) battle.ReleaseDungeon(row.Actor, instance);
        }
    }

    sealed class Authority : DungeonCombatAuthority;
}
