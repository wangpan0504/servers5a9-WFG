using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    public static void RunBattlePassHellCosmoCompletionCore()
    {
        int before = Count;
        var clock = new FormalClearClock(new(2026, 11, 1, 7, 0, 0, TimeSpan.FromHours(8)));
        var role = new Character(1, 0, "HellCore", 0, Location, Location);
        var (ordinary, ordinaryAuthority) = CreateRun(role, e => e with { QuestId = 0, ActiveQuestSlot = byte.MaxValue, RouteQuestId = 0 });
        var normal = ordinary.PrepareConfirmedDeath(ordinaryAuthority.Death(ordinary.Snapshot.Identity, 20)).Pending!;
        normal.FreezeBattlePassHellCosmoCompletion(ordinary, clock);
        That(normal.BattlePassHellCosmoCompletion is null, "普通终点或RoomClearFact不能冒深渊/超星空完成");
        var (run, authority) = CreateRun(role, e => e with { QuestId = 0, ActiveQuestSlot = byte.MaxValue, RouteQuestId = 0, IsCosmo = true });
        var pending = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 20)).Pending!;
        pending.FreezeBattlePassHellCosmoCompletion(run, clock);
        var fact = pending.BattlePassHellCosmoCompletion!;
        That(fact is not null && fact.IsCosmo && fact.Matches(run, pending), "原pending真实终末ClearFact生成超星空typed完成");
        var planned = fact!.PlannedAt;
        clock.Now = clock.Now.AddSeconds(2); pending.FreezeBattlePassHellCosmoCompletion(run, clock);
        That(ReferenceEquals(fact, pending.BattlePassHellCosmoCompletion) && fact.PlannedAt == planned, "同pending重试不重冻来源和原计划时间");
        That(!fact.Matches(run, new(pending.Death, pending.Monster, pending.Result)), "复制pending对象不借原权威完成");
        var map = ordinary.Entry.StartMap;
        var hellEntry = ordinary.Entry with { HellParty = new(map.Id, map.Position) };
        var wave = new DungeonHellWaveMonster(0, 1, 2, 3, 0, 21, 0);
        var monster = new DungeonPlannedMonster(1, 21, 2, 3, null) { HellWave = wave };
        var plan = ordinary.CurrentMonsterPlan with { Monsters = [ordinary.CurrentMonsterPlan.Monsters[0], monster], HellWaves = [wave] };
        var complete = ordinary.Snapshot with { LivingMonsters = [] };
        That(BattlePassHellCosmoCompletion.IsHellRoomComplete(hellEntry, plan, complete), "真实hellmap全部blocking和全部波次完成才认整次");
        foreach (var remaining in new ushort[][] { [20], [21], [20, 21] })
            That(!BattlePassHellCosmoCompletion.IsHellRoomComplete(hellEntry, plan, complete with { LivingMonsters = remaining }), "基础怪或任一波次未清不能冒完整深渊");
        That(!BattlePassHellCosmoCompletion.IsHellRoomComplete(ordinary.Entry, plan, complete) &&
            !BattlePassHellCosmoCompletion.IsHellRoomComplete(hellEntry, plan with { HellWaves = [] }, complete) &&
            !BattlePassHellCosmoCompletion.IsHellRoomComplete(hellEntry with { HellParty = new(map.Id + 1, map.Position) }, plan, complete) &&
            !BattlePassHellCosmoCompletion.IsHellRoomComplete(hellEntry, plan, complete with { Phase = SinglePlayerDungeonPhase.Settlement }),
            "普通房/没有原波次/非真实hellmap/非原Dungeon阶段全部拒绝");
        foreach (var row in new[] { (false, 0, 23, 1, 100, 23), (false, 1, 40, 3, 1000, 23),
            (true, 1, 29, 3, 1000, 24), (true, 2, 53, 80, 1600, 24) })
        {
            var mission = new BattlePassMission(row.Item3, row.Item4, row.Item5, row.Item6, "", -1);
            That(BattlePassHellCosmoCompletion.MatchesDefinition(row.Item1, (byte)row.Item2, mission) &&
                !BattlePassHellCosmoCompletion.MatchesDefinition(!row.Item1, (byte)row.Item2, mission) &&
                !BattlePassHellCosmoCompletion.MatchesDefinition(row.Item1, (byte)((row.Item2 + 1) % 3), mission) &&
                !BattlePassHellCosmoCompletion.MatchesDefinition(row.Item1, (byte)row.Item2, mission with { RawParameter = 0 }) &&
                !BattlePassHellCosmoCompletion.MatchesDefinition(row.Item1, (byte)row.Item2, mission with { RequiredCount = row.Item4 + 1 }) &&
                !BattlePassHellCosmoCompletion.MatchesDefinition(row.Item1, (byte)row.Item2, mission with { Experience = row.Item5 + 1 }), "四原任务严格分类/参数/数量/XP且不串深渊种类");
        }
        var id = pending.Death.Identity;
        string key = BattlePassHellCosmoCompletion.Identity("cosmo_completion", id, run.Entry.DungeonId);
        That(key.Length == 32 && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            key == BattlePassHellCosmoCompletion.Identity("cosmo_completion", id with { Epoch = id.Epoch + 1 }, run.Entry.DungeonId), "同实例后继epoch不变完整32hex整次键");
        foreach (var other in new[] { id with { CharacterId = 2 }, id with { InstanceId = Guid.NewGuid() }, id with { TaskAttempt = id.TaskAttempt + 1 } })
            That(key != BattlePassHellCosmoCompletion.Identity("cosmo_completion", other, run.Entry.DungeonId), "实例角色和attempt独立隔离");
        That(key != BattlePassHellCosmoCompletion.Identity("normal_hell_completion", id, run.Entry.DungeonId), "两真实完成域来源隔离");
        Console.WriteLine($"PASS BattlePass Hell/Cosmo completion source core ({Count - before} checks)");
    }
}
