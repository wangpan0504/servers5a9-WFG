using DfoServer.Game.BattlePass;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Dungeons;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 使用真实排期中启用零扣币通关任务的日期，避免未装配战令掩盖来源校验。
internal static class DungeonBattlePassRegression
{
    internal static (BattlePassMissionPolicy Missions, TimeProvider Clock) Schedule(PvfArchive pvf)
    {
        var start = new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.FromHours(8));
        var missions = new BattlePassMissionPolicy("dungeon-clear-regression", start, BattlePassCatalog.Load(pvf));
        var now = Enumerable.Range(0, missions.Days).Select(day => start.AddDays(day).AddHours(1))
            .First(at => missions.Active(at).Any(row => row.Definition.RawTypeCode == 5 && row.Definition.Id is 6 or 37));
        return (missions, new Testing.Inventory.DailyBoosterTestClock(now));
    }

    internal static void Enable(QuestSceneTests.Fixture fixture, BattlePassMissionPolicy missions, TimeProvider clock)
    {
        fixture.Repository.ConfigureBattlePassMissions(missions);
        That(fixture.Repository.ReadBattlePassMissions(fixture.Owner, clock.GetUtcNow())!.Slots.Any(),
            "通关回归实际启用战令排期及任务");
    }

    internal static void FormalClearOnce(QuestSceneTests.Fixture fixture) =>
        That(Convert.ToInt64(fixture.Query("SELECT COUNT(*) FROM account_battle_pass_source_events WHERE source_kind='formal_dungeon_clear'")) == 1,
            "正式整场战令来源只提交一次");

    internal static void RunResources(TcpTestResources resources)
    {
        var pvf = resources.Archive;
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var growth = SelfTestCatalogs.Growth(pvf, resources.Learning);
        var kills = SelfTestCatalogs.KillExperience(pvf);
        var clear = SelfTestCatalogs.ClearExperience(pvf);
        foreach (int id in new[] { 5101, 530 })
        {
            int before = Count;
            var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == id));
            var options = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options;
            var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(new[] { 6030, 6080, 6082, 10112 })
                .Concat(options.Where(row => row.DungeonId == id && !row.InProgress && row.QuestId > 0).Select(row => row.QuestId))
                .Distinct().ToArray();
            var bounds = area.Map(done.ToHashSet()).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town, area.Area, (ushort)(bounds.X + bounds.Width / 2), (ushort)(bounds.Y + bounds.Height / 2));
            var (missions, clock) = Schedule(pvf);
            using var fixture = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning, growth,
                resources.Quests, false, 95, done, position: position, items: [(10161610, 100)], timeProvider: clock);
            Enable(fixture, missions, clock);
            byte difficulty = (byte)Enumerable.Range(0, 5).First(value => dungeons.Get(id).AllowsDifficulty((byte)value, false));
            var receipt = fixture.Session.EnterFormalDungeon((uint)id, 0, difficulty, dungeons, rooms, pieces: resources.ItemResources.EpicPieces);
            That(receipt is not null, $"战令回归沿真实准入进入副本{id}: {fixture.Session.LastFormalDungeonRejection}");
            var path = QuestStoryRouteTests.Path(receipt!.Entry.Maze.StaticTopology, receipt.Entry.StartMap.Position, receipt.Entry.BossMap.Position);
            foreach (var point in path)
            {
                receipt = fixture.Session.CompleteFormalDungeonLoading()!;
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly)
                    .OrderBy(row => row.IsBoss).ToArray())
                {
                    var death = fixture.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth);
                    That(death is { Outcome: DungeonRunOutcome.Accepted, Receipt: not null }, $"副本{id}权威死亡提交");
                    receipt = death.Receipt!;
                }
                if (point == receipt.Entry.BossMap.Position) break;
                var next = path[Array.IndexOf(path, point) + 1];
                That(fixture.Session.MoveFormalDungeon((byte)next.X, (byte)next.Y) is not null, $"副本{id}真实路线过房");
            }
            That(fixture.Session.SettleFormalDungeon(fixture.Read().Id, 99, 99, [1, 2, 3], dungeons, clear, growth) is
                { Outcome: DungeonRunOutcome.Accepted, Receipt: not null }, $"副本{id}战令开启后结算不抛异常");
            FormalClearOnce(fixture);
            var committed = fixture.Read();
            That(fixture.Session.SettleFormalDungeon(committed.Id, 99, 99, [1, 2, 3], dungeons, clear, growth).Outcome == DungeonRunOutcome.Duplicate &&
                QuestCompletionRepositoryTests.Same(committed, fixture.Read()), $"副本{id}重复报告不重奖");
            FormalClearOnce(fixture);
            Console.WriteLine($"PASS dungeon-clear-battle-pass dungeon={id} checks={Count - before}");
        }
    }
}
