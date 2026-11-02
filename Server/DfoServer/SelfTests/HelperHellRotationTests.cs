using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class HelperHellRotationTests
{
    internal static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int startChecks = Count;
        var growth = SelfTestCatalogs.Growth(pvf, skills);
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var kills = SelfTestCatalogs.KillExperience(pvf);
        var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 291100308));
        var done = new HashSet<int>(area.Quests) { 6080 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(walk.X + walk.Width / 2)), checked((ushort)(walk.Y + walk.Height / 2)));

        QuestSceneTests.Fixture Create(int tickets, ushort? fatigue = null) => new(towns, characters, skills,
            growth, quests, false, 95, done.Order().ToArray(), position: position,
            items: [(3330, tickets)], fatigueRemaining: fatigue);
        int Tickets(QuestSceneTests.Fixture fixture) => fixture.Read().Inventory.Items.Where(row => row.Core.ItemId == 3330).Sum(row => row.Core.Count);
        FormalDungeonReceipt Clear(QuestSceneTests.Fixture fixture, FormalDungeonReceipt current)
        {
            foreach (var monster in current.Monsters.Monsters.Where(row => row.IsBlocking && current.Snapshot.LivingMonsters.Contains(row.ObjectKey)))
            {
                var id = fixture.Read().Id;
                var death = fixture.Session.ObserveFormalDungeonDeath(new(monster.ObjectKey, id, 0x0111,
                    id, 600, 3, 0, (uint)monster.CandidateLevel, 504, 620, 3, true, new string('D', 64)), kills, growth);
                That(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null,
                    "助手轮换测试通过真实死亡事务清除阻挡怪及全部深渊波次");
                current = death.Receipt!;
            }
            return current;
        }
        DungeonPoint Door(FormalDungeonReceipt current) => (current.Entry.Maze.StaticTopology.Links ?? [])
            .Select(link => link.First == current.Snapshot.CurrentMap.Position ? (DungeonPoint?)link.Second :
                link.Second == current.Snapshot.CurrentMap.Position ? link.First : null).First(point => point is not null)!.Value;
        FormalDungeonReceipt ReachPillar(QuestSceneTests.Fixture fixture, out int visits)
        {
            var entry = fixture.Session.EnterFormalDungeon(291100308, 0, 4, dungeons, rooms,
                pieces: items.EpicPieces, draw: _ => 0, hellParty: true);
            That(entry is not null && entry.Entry.StartMap.Position != entry.Entry.HellParty!.Room,
                "首轮保留正常入口，不直接落柱子房：" + fixture.Session.LastFormalDungeonRejection);
            var path = HellPartyEntryTests.RiftPath(entry!.Entry.Maze, entry.Entry.StartMap.Position, entry.Entry.HellParty!.Room);
            visits = path.Count + 1;
            var current = fixture.Session.CompleteFormalDungeonLoading()!;
            foreach (var target in path)
            {
                current = Clear(fixture, current);
                That(fixture.Session.MoveFormalDungeon((byte)target.X, (byte)target.Y) is not null, "首轮沿迷宫真实门抵达柱子房");
                current = fixture.Session.CompleteFormalDungeonLoading()!;
            }
            That(current.Snapshot.CurrentMap.Position == entry.Entry.HellParty.Room, "首轮正常到达真实柱子房");
            return current;
        }

        int firstVisits;
        using (var fixture = Create(120))
        {
            var first = ReachPillar(fixture, out firstVisits);
            var id = fixture.Read().Id;
            var door = Door(first);
            That(fixture.Session.SetHelperHellRotation(id, true) is { Accepted: true, HellRotation: true }, "独立开启轮换");
            var blocked = fixture.Session.RotateHelperHell((byte)door.X, (byte)door.Y, 0, dungeons, rooms, pieces: items.EpicPieces);
            That(blocked.Attempted && blocked.Receipt is null && Tickets(fixture) == 90,
                "柱子房还有阻挡怪或深渊波次时不轮换、不扣票");
            first = Clear(fixture, first);
            That(fixture.Session.SetHelperHellRotation(id, false) is { Accepted: true, HellRotation: false } &&
                !fixture.Session.RotateHelperHell((byte)door.X, (byte)door.Y, 0, dungeons, rooms).Attempted,
                "关闭立即停用轮换资格，真实门仍由普通流程处理");
            fixture.Session.SetHelperHellRotation(id, true);
            var before = fixture.Read();
            var second = fixture.Session.RotateHelperHell((byte)door.X, (byte)door.Y, 0, dungeons, rooms,
                pieces: items.EpicPieces).Receipt;
            That(second is not null && second.Entry.DungeonId != first.Entry.DungeonId &&
                second.Snapshot.CurrentMap.Position == second.Entry.HellParty!.Room &&
                second.Entry.StartMap == second.Snapshot.CurrentMap && second.Entry.BasisLevel == first.Entry.BasisLevel &&
                second.Entry.MonsterBasisLevel == first.Entry.MonsterBasisLevel && second.Entry.Difficulty == first.Entry.Difficulty &&
                second.Snapshot.Identity.InstanceId != first.Snapshot.Identity.InstanceId && Tickets(fixture) == 60,
                "轮换准备并提交新实例，直入同地区另一柱子房，冻结首轮等级难度并正常扣票：" + fixture.Session.LastFormalDungeonRejection);
            That(fixture.Read().Fatigue!.Remaining == before.Fatigue!.Remaining &&
                !fixture.Session.RotateHelperHell((byte)door.X, (byte)door.Y, 0, dungeons, rooms).Attempted && Tickets(fixture) == 60,
                "完整加载前重复旧门不再扣票，房间疲劳不提前重复计算");
            var loaded = fixture.Session.CompleteFormalDungeonLoading();
            That(loaded is not null && fixture.Read().Fatigue!.Remaining + 1 == before.Fatigue!.Remaining,
                "新柱子房加载完成后沿正常链只扣一次房间疲劳");
            loaded = Clear(fixture, loaded!);
            var nextDoor = Door(loaded);
            var third = fixture.Session.RotateHelperHell((byte)nextDoor.X, (byte)nextDoor.Y, 0, dungeons, rooms,
                pieces: items.EpicPieces).Receipt;
            That(third is not null && third.Entry.DungeonId != second!.Entry.DungeonId && Tickets(fixture) == 30 &&
                third.Entry.BasisLevel == first.Entry.BasisLevel && third.Entry.HelperRotation == second.Entry.HelperRotation,
                "连续第二次轮换继续另一个地下城并沿用首轮冻结依据");
            That(fixture.Session.SetHelperHellRotation((ushort)(id + 1), true) is null, "错误角色不能改写当前轮换偏好");
            fixture.Session.HasMultiplePartyMembers = () => true;
            That(fixture.Session.SetHelperHellRotation(id, true) is { Accepted: false }, "多人状态不能开启轮换");
            That(fixture.Session.SetHelperHellRotation(id, false) is { Accepted: true, HellRotation: false },
                "组队后仍能明确关闭轮换，不保留隐蔽开启状态");
        }
        foreach (bool exhausted in new[] { false, true })
        {
            using var fixture = Create(exhausted ? 60 : 30, exhausted ? checked((ushort)firstVisits) : null);
            var current = Clear(fixture, ReachPillar(fixture, out _));
            fixture.Session.SetHelperHellRotation(fixture.Read().Id, true);
            var before = fixture.Read(); var door = Door(current);
            var result = fixture.Session.RotateHelperHell((byte)door.X, (byte)door.Y, 0, dungeons, rooms,
                pieces: items.EpicPieces);
            That(result.Attempted && result.Receipt is null && fixture.Read().Revision == before.Revision &&
                fixture.Session.CurrentFormalDungeonScene()!.Snapshot.Identity == current.Snapshot.Identity &&
                Tickets(fixture) == (exhausted ? 30 : 0),
                exhausted ? "疲劳不足保留原柱子房及票数，不额外计算费用" : "门票不足保留原柱子房，不降级为普通走门");
        }
        Console.WriteLine("HelperHellRotationTests: " + (Count - startChecks) + " checks");
    }

}
