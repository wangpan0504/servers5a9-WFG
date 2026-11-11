using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SinglePlayerDungeonRunTests
{
    static void VerifyChampionRun(DungeonEntryPlan entry, DungeonFixedRoute defaultRoute, FixtureCombatAuthority authority)
    {
        var championEntry = entry with { ChampionPercents = new(100, 100, 100, 100) };
        var championRoute = defaultRoute with { Rooms = defaultRoute.Rooms.Select(room => room.WithChampionRolls(championEntry.ChampionPercents, 0, _ => 0)).ToArray() };
        That(SinglePlayerDungeonRun.TryCreate(championEntry, 3, 300, championRoute, authority, out _, out _) &&
            !SinglePlayerDungeonRun.TryCreate(entry, 3, 300, championRoute, authority, out _, out _) &&
            !SinglePlayerDungeonRun.TryCreate(championEntry with { NamedMonsters = [1] }, 3, 300, championRoute, authority, out _, out _) &&
            !SinglePlayerDungeonRun.TryCreate(entry with { ChampionPercents = default }, 3, 300, championRoute, authority, out _, out _),
            "有精英概率声明的普通怪升级仍能建立实例；没有声明或概率为零时不能伪造精英");
        var excessChampions = defaultRoute.Rooms[0] with
        {
            Monsters = defaultRoute.Rooms[0].Monsters.Select(monster => monster with { SpawnType = 1 }).ToArray()
        };
        That(!SinglePlayerDungeonRun.TryCreate(championEntry, 3, 300,
            championRoute with { Rooms = [excessChampions, .. championRoute.Rooms.Skip(1)] }, authority, out _, out _),
            "每房随机提升至多一只，不能借合法概率伪造全房精英");
    }

    internal static void RunChampionCore()
    {
        var kills = DungeonKillExperienceCatalog.Parse([new(0, 3), new(0, 100)],
            [new(3, 1), new(0, 1), new(0, 1), new(0, 1), new(0, 1), new(0, 1),
             new(3, 2), new(0, 1), new(0, 5), new(0, 7), new(0, 9)],
            id => id == 1 ? "[dungeon difficulty exp bonusrate]" : "[monster exp bonusrate]");
        var start = new DungeonMap("map", new(0, 0), 1, "map/champion-start.map");
        var middle = new DungeonMap("map", new(1, 0), 2, "map/champion-middle.map");
        var boss = new DungeonMap("boss", new(2, 0), 3, "map/champion-boss.map");
        var maze = new DungeonMaze(0, 3, 1, "BBFFEE", [start.Position], [boss.Position], [start, middle, boss], []);
        var route = new DungeonFixedRoute([Plan(start, 10, 11), WithTeams(Plan(middle, 12), 0), BossPlan(boss, 13)],
            [new(start.Position, middle.Position), new(middle.Position, boss.Position)]);
        foreach (uint id in new uint[] { 3, 400000097, 400000098 })
        {
            var entry = new DungeonEntryPlan(99, 17, id, 0, byte.MaxValue, 0, maze, start, boss);
            var authority = new FixtureCombatAuthority();
            VerifyChampionRun(entry, route, authority);
            var rates = new DungeonChampionRates(100, 100, 100, 100);
            var promoted = route.Rooms[0].WithChampionRolls(rates, 0, _ => 0);
            var frozen = route with { Rooms = [promoted, .. route.Rooms.Skip(1)] };
            That(SinglePlayerDungeonRun.TryCreate(entry with { ChampionPercents = rates }, 1, 1,
                frozen, authority, out var created, out _), "普通和两星河接受单只随机精英，不再给予全体提升豁免");
            var run = created!;
            run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
            var first = authority.Death(run.Snapshot.Identity, 10, 1);
            var elite = run.PrepareConfirmedDeath(first).Pending!;
            That(elite.Monster.RewardType == 1 && DungeonKillExperiencePolicy.Plan(run, elite, kills, 3).Amount == 500,
                "实际精英品级进入既有经验倍率，不把恢复0–1精英说成奖励完全不变");
            run.ConfirmCommittedDeath(elite);
            var normal = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 11, 2)).Pending!;
            That(normal.Monster.RewardType == 0 && DungeonKillExperiencePolicy.Plan(run, normal, kills, 3).Amount == 100,
                "同房未提升演员保持普通品级经验，不借精英模板或缓存继承倍率");
            run.ConfirmCommittedDeath(normal);
            run.Move(run.Snapshot.Identity, Guid.NewGuid(), middle.Position);
            run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
            run.Move(run.Snapshot.Identity, Guid.NewGuid(), start.Position);
            That(run.Snapshot.LivingMonsters.Count == 0 && promoted.Monsters[0].SpawnType == 1 &&
                promoted.Monsters[1].SpawnType == 0 &&
                run.CurrentMonsterPlan.Monsters.Select(monster => (monster.ObjectKey, monster.SpawnType, monster.CandidateLevel))
                    .SequenceEqual(promoted.Monsters.Select(monster => (monster.ObjectKey, monster.SpawnType, monster.CandidateLevel))) &&
                run.AcceptConfirmedDeath(first).Outcome == DungeonRunOutcome.Duplicate,
                "回访保留原精英品级及死亡集，不重新抽取或重复提交死亡");
        }
    }
}
