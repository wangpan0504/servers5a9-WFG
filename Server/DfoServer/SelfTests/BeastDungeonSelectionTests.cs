using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BeastDungeonSelectionTests
{
    public static void Run(TcpTestResources resources)
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-beast-selection-" + Guid.NewGuid().ToString("N"));
        var repository = new CharacterRepository(Path.Combine(root, "game.db"), resources.Quests);
        var owners = new AccountOwners();
        try
        {
            foreach (var (type, qualified, town) in new[]
            {
                (22, true, (byte)31), (22, false, (byte)31), (40, true, (byte)31),
                (56, true, (byte)31), (23, true, (byte)31), (22, true, (byte)38), (22, true, (byte)51)
            })
            {
                using var owner = owners.Acquire($"beast-select-{type}-{qualified}-{town}", () => { });
                var area = resources.Towns.Areas.Values.First(row => row.Town == town && row.DungeonWorldMap is not null);
                var done = new HashSet<int>(area.Quests) { 4840, 12829, 3779 };
                if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
                done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
                if (qualified) done.Add(5927); else done.Remove(5927);
                var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
                var position = new Location(area.Town, area.Area, (ushort)(bounds.X + bounds.Width / 2), (ushort)(bounds.Y + bounds.Height / 2));
                var candidate = CharacterCreation.Prepare($"BeastS{type}{town}{(qualified ? 1 : 0)}", resources.Characters.Get(0), position, position, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
                repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = done.ToArray(), InitialLearning = learning });
                var clock = new DailyBoosterTestClock(new DateTimeOffset(2026, 10, 26, 5, 59, 59, TimeSpan.FromHours(8)));
                var entry = new ChannelEntry(new(2, 1), type, "[normal]");
                var session = new GameSession(repository, resources.Towns, owner, questCatalog: resources.Quests, entry: entry, timeProvider: clock);
                session.Select(0, resumeTown: true);
                That(session.CaptureBeastDungeonSelection() is null, "未就绪和非选图阶段不提供入侵名单");
                session.MarkReady();
                That(session.CaptureBeastDungeonSelection() is null, "城镇阶段不提供入侵名单");
                var pairBefore = BeastDungeonRotation.Pair(BeastDungeonRotation.ServiceDay(clock.GetUtcNow()));
                var catalog = SelfTestCatalogs.Dungeons(resources.Archive);
                var rooms = SelfTestCatalogs.Rooms(resources.Archive);
                That(session.EnterFormalDungeon((uint)pairBefore.First, 0, 4, catalog, rooms, beastMode: true) is null &&
                    session.LastFormalDungeonRejection == "beast-native-selection-required",
                    "native-beast-admission: 原图魔兽伪造直入不能跳过正式选图上下文");
                var selection = session.BeginDungeonSelection();
                bool eligible = type == 22 && qualified && town == 31;
                var captured = session.CaptureBeastDungeonSelection();
                That((captured is not null) == eligible, $"第一版门禁按普通频道、原城镇和5927隔离：{type}/{qualified}/{town}");
                if (!eligible)
                {
                    That(session.EnterFormalDungeon((uint)pairBefore.First, 0, 4, catalog, rooms, beastMode: true) is null &&
                        session.LastFormalDungeonRejection == "beast-native-selection-required" &&
                        repository.LoadBeastDungeonUsed(owner, session.Selected!.Id,
                            BeastDungeonRotation.ServiceDay(clock.GetUtcNow()), pairBefore.First) == 0,
                        $"原图入场独立核频道、资格与区域且不扣次：{type}/{qualified}/{town}");
                    continue;
                }
                That(selection is not null && captured!.Counts.Length == 2 && captured.Counts.All(row => row.Remaining == 2),
                    "原中央公园仅两图满额，普通五图次数不混入快照");
                var pair = BeastDungeonRotation.Pair(captured!.Day);
                That(captured.Counts.Select(row => row.Dungeon).SequenceEqual(new[] { (uint)pair.First, (uint)pair.Second }) &&
                    session.CanPublishBeastDungeonSelection(captured), "入侵顺序与服务日轮换一致且可发布");
                That(repository.CommitBeastDungeonEntry(owner, session.Selected!.Id, captured.Day, pair.First) == 1 &&
                    repository.CommitBeastDungeonEntry(owner, session.Selected.Id, captured.Day, pair.First) == 2,
                    "隔离库模拟本图两次正式额度消费");
                var exhausted = session.CaptureBeastDungeonSelection()!;
                That(exhausted.Counts[0] == ((uint)pair.First, (byte)0) && exhausted.Counts[1].Remaining == 2,
                    "本图耗尽仍保留两图标识，另一图独立满额");
                clock.Advance(TimeSpan.FromSeconds(1));
                That(!session.CanPublishBeastDungeonSelection(exhausted), "06点后的旧日快照不可发布");
                var next = session.CaptureBeastDungeonSelection()!;
                That(next.Day == exhausted.Day + 1 && next.Counts.All(row => row.Remaining == 2) &&
                    !next.Counts.Select(row => row.Dungeon).SequenceEqual(exhausted.Counts.Select(row => row.Dungeon)),
                    "06点新名单与新次数同日生成");
                session.ReturnFromDungeonSelection(restorePosition: false);
                That(!session.CanPublishBeastDungeonSelection(next) && session.CaptureBeastDungeonSelection() is null,
                    "退出选图后丢弃原名单发布资格");
                session.BeginDungeonSelection();
                That(!session.CanPublishBeastDungeonSelection(next) && session.CaptureBeastDungeonSelection() is not null,
                    "同角色重开选图不可重用旧选择身份");
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - before} beast-selection assertions");
    }
}
