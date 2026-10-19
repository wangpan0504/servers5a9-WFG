using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 主线6210/回放6216「罪恶」：夜之摩天楼中心 dummy Boss 猎杀。资源与合成死亡断言不代替实机验收。
internal static class PreyIsysSinQuestTests
{
    internal const int QuestId = 6210, ReplayQuestId = 6216, PreviousQuestId = 6209, ReplayPreviousQuestId = 6215;
    internal const int DungeonId = 291100374, HuntTarget = 107009977, Praxess = 107009968, BossMapId = 292106200;

    public static void RunResources(PvfArchive pvf, QuestCatalog? sharedQuests = null)
    {
        int checks = Count;
        var quests = sharedQuests ?? global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        foreach (var sample in new[] { (Quest: QuestId, Previous: PreviousQuestId), (Quest: ReplayQuestId, Previous: ReplayPreviousQuestId) })
        {
            var quest = quests.Get(sample.Quest);
            string name = quest.Definition?.Values("name").FirstOrDefault().Text ?? "";
            That(quest.Grade == "[epic]" && quest.Condition == "[hunt monster]" && quest.SubType == -1 &&
                quest.MinimumLevel == 95 && quest.CompleteNpc == 8004 && quest.Prerequisites.SequenceEqual([sample.Previous]) &&
                name == "罪恶" && quest.IntData.SequenceEqual([DungeonId, -1, HuntTarget, 1]) &&
                quest.Definition is { Diagnostics.Length: 0, Dungeons: [{ DungeonId: DungeonId, Parameter: -1 }] },
                "真实罪恶四元组与交付NPC保留：" + sample.Quest);
            That(QuestCoverage.Unconfirmed(quest).IsEmpty && QuestCoverage.HasOrdinaryDeclarations(quest) &&
                QuestEligibility.InspectResource(quest).Entry == QuestResourceEntry.Ordinary &&
                QuestHuntProgress.TryTargets(quest, out var targets) && targets is
                    [{ DungeonId: DungeonId, MinimumDifficulty: -1, MonsterId: HuntTarget, Count: 1, EnemyType: 1 }] &&
                QuestHuntProgress.Initial(quest) == 1,
                "罪恶猎杀走普通权威事件，初始scalar为1：" + sample.Quest);
            var definition = dungeons.Get(DungeonId);
            That(definition.MinimumLevel == 95 && definition.BasisLevel == 100 && definition.Mazes.Count >= 4,
                "夜之摩天楼中心保留故事迷宫：" + sample.Quest);
            var pending = new Character(17, 0, "Sin", 1, new Location(38, 2, 100, 200, 0), null)
            {
                Level = 95,
                CompletedQuests = [sample.Previous],
                ActiveMainQuests = [new(5, checked((ushort)sample.Quest), QuestHuntProgress.Initial(quest)) { AcceptanceId = Guid.NewGuid() }]
            };
            foreach (uint requested in new uint[] { checked((uint)sample.Quest), 0 })
            {
                That(DungeonEntryPlan.TryCreate(dungeons, pending, DungeonId, requested, 0, out var plan, out var reason,
                    quest: requested == 0 ? null : quest, quests: quests) && plan is
                    { Maze.ConnectionQuestId: var connection, StartMap.Id: 292106196, BossMap.Id: BossMapId } &&
                    connection == sample.Quest && plan.Maze.StaticTopology.Supported && plan.BasisLevel == 100,
                    "罪恶显式及自动请求选择中心迷宫：" + sample.Quest + "/" + requested + "/" + reason);
                var failures = new List<string>();
                var planned = new List<DungeonMonsterPlan>();
                ushort next = 100;
                foreach (var map in plan!.Maze.MapSpecification!)
                {
                    try
                    {
                        var actors = DungeonMonsterPlan.FromRoom(rooms.Get(map, requirePassiveDefinitions: false),
                            pending.Id, next, plan.BasisLevel, playerTeam: plan.PlayerTeam);
                        planned.Add(actors);
                        if (actors.Monsters.Count > 0 || actors.SpecialObjects.Count > 0)
                            next = checked((ushort)actors.NextObjectKey);
                    }
                    catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException or OverflowException)
                    {
                        failures.Add($"{map.Id}: {error.Message}");
                    }
                }
                That(failures.Count == 0, "罪恶整条迷宫可以生成：" + sample.Quest + " " + string.Join(" | ", failures));
                var boss = planned.Single(room => room.Room.Map.Id == BossMapId);
                var dummy = boss.Monsters.Single(row => row.MonsterId == HuntTarget);
                var praxess = boss.Monsters.Single(row => row.MonsterId == Praxess);
                That(dummy is { IsScriptedBoss: true, IsBoss: true, CanReportDeath: true, IsBlocking: true, IsFriendly: false, SpawnType: 3, Team: 0 } &&
                    dummy.Source!.Modifiers.SequenceEqual(["[dummy]"]) &&
                    praxess is { IsScriptedBoss: false, IsBlocking: false, CanReportDeath: false, IsFriendly: true, SpawnType: 0, Team: 0 },
                    "292106200的team0 dummy加德拉肯仍可报告死亡，普勒瑟斯保持友方演员：" + sample.Quest);
                var active = pending.ActiveMainQuests.Single();
                That(QuestHuntProgress.Bound(active, plan) &&
                    QuestHuntProgress.MonsterDeath([active], plan, quests, dummy).Single().ConditionState == 0 &&
                    QuestHuntProgress.MonsterDeath([active], plan, quests, praxess).Single().ConditionState == 1,
                    "只有dummy加德拉肯死亡归零罪恶scalar，普勒瑟斯不能替代：" + sample.Quest);
            }
        }
        Console.WriteLine($"PASS {Count - checks} 夜之摩天楼中心罪恶猎杀资源、入场与dummy权威死亡断言（非实机）");
    }
}
