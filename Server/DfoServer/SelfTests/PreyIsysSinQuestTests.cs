using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 主线6210/回放6216「罪恶」：夜之摩天楼中心 dummy Boss 猎杀。资源与合成死亡断言不代替实机验收。
internal static class PreyIsysSinQuestTests
{
    internal const int QuestId = 6210, ReplayQuestId = 6216, PreviousQuestId = 6209, ReplayPreviousQuestId = 6215;
    internal const int DungeonId = 291100374, HuntTarget = 107009977, Praxess = 107009968, BossMapId = 292106200;

    // 完整服务端提交链使用明确合成的脚本报告；不把CMT3112的DESTROY声明当作客户端发包证明。
    public static void RunSession(TownCatalog towns, CharacterCatalog characters, ItemResources items, QuestCatalog quests,
        SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
        DungeonKillExperienceCatalog kills, DungeonClearExperienceCatalog clears, QuestRewardParameterCatalog parameters)
    {
        int checks = Count;
        foreach (var sample in new[] { (Quest: QuestId, Request: (uint)QuestId), (Quest: ReplayQuestId, Request: 0u) })
        {
            var quest = quests.Get(sample.Quest);
            var entrance = MiddleQuestEntryTests.Entrance(towns, quest, DungeonId);
            using var f = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
                entrance.Completed, position: entrance.Position);
            ushort id = checked((ushort)sample.Quest);
            That(f.Session.ChangeQuest(id, QuestLifecycleAction.Accept, quests, items.Items, items.GoldLimits, items.EpicPieces)?.Commit is not null,
                "罪恶按真实等级和前置接取：" + id);
            var accepted = f.Read().ActiveMainQuests.Single(row => row.QuestId == id);
            var opening = f.Session.EnterFormalDungeon(DungeonId, sample.Request, 0, dungeons, rooms);
            That(opening is not null && opening.Entry.RouteQuestId == id && opening.Entry.BossMap.Id == BossMapId,
                "罪恶显式及自动入口选中原任务末图：" + f.Session.LastFormalDungeonRejection);
            var path = QuestStoryRouteTests.Path(opening!.Entry.Maze.StaticTopology, opening.Entry.StartMap.Position, opening.Entry.BossMap.Position);
            FormalDungeonReceipt loaded = opening;
            for (int at = 0; at < path.Length; at++)
            {
                loaded = f.Session.CompleteFormalDungeonLoading()!;
                if (at == path.Length - 1) break;
                foreach (var monster in loaded.Monsters.Monsters.Where(row => row.IsBlocking))
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded, monster), kills, growth).Outcome == DungeonRunOutcome.Accepted,
                        "罪恶前序房间按当前真实演员key清房");
                That(f.Session.MoveFormalDungeon(checked((byte)path[at + 1].X), checked((byte)path[at + 1].Y)) is not null,
                    "罪恶沿真实门墙移动");
            }
            var boss = loaded.Monsters.Monsters.Single(row => row.MonsterId == HuntTarget);
            var friendly = loaded.Monsters.Monsters.Single(row => row.MonsterId == Praxess);
            That(loaded.Snapshot.CurrentMap.Id == BossMapId && boss is { SourceOrdinal: 0, IsBlocking: true, CanReportDeath: true, IsFriendly: false } &&
                friendly is { SourceOrdinal: 1, IsBlocking: false, CanReportDeath: false, IsFriendly: true },
                "罪恶保留468的dummy Boss资格及友方普勒瑟斯");
            var before = f.Read();
            That(f.Session.EnableFormalDungeonSettlement() is null &&
                f.Session.FinishQuest(id, ushort.MaxValue, quests, parameters, items.Items, items.GoldLimits, items.EpicPieces, growth).Outcome == QuestFinishOutcome.Rejected,
                "罪恶目标报告前不能提前结算或交付");
            That(f.Session.ObserveFormalDungeonScriptedDeath(new(boss.ObjectKey, 0x22, false, new string('A', 64)), kills, growth).Outcome == DungeonRunOutcome.Rejected &&
                QuestCompletionRepositoryTests.Same(before, f.Read()), "罪恶坏摘要不落库");
            var retire = f.Session.ObserveFormalDungeonScriptedDeath(new(friendly.ObjectKey, 0x22, true, new string('B', 64)), kills, growth);
            That(retire is { Outcome: DungeonRunOutcome.Accepted, ClearFact: null, Reward: null } &&
                QuestCompletionRepositoryTests.Same(before, f.Read()) && f.Session.Phase == SessionPhase.Dungeon,
                "普勒瑟斯退场不能冒充目标死亡或生成通关");
            var report = new NativeDungeonScriptedDeathObservation(boss.ObjectKey, 0x22, true, new string('C', 64));
            var death = f.Session.ObserveFormalDungeonScriptedDeath(report, kills, growth);
            That(death is { Outcome: DungeonRunOutcome.Accepted, ClearFact.ClearMap.Id: BossMapId } &&
                f.Session.Phase == SessionPhase.Settlement, "罪恶合法目标报告生成真实末图通关事实并进入结算");
            That(f.Read().ActiveMainQuests.Single(row => row.QuestId == id) == accepted with { ConditionState = 0 },
                "罪恶猎杀归零与原接取身份同事务保存，不要求清图任务专用凭据");
            before = f.Read();
            That(f.Session.ObserveFormalDungeonScriptedDeath(report, kills, growth).Outcome == DungeonRunOutcome.Duplicate &&
                QuestCompletionRepositoryTests.Same(before, f.Read()), "罪恶重复目标报告不重复推进或发奖");
            var enabled = f.Session.EnableFormalDungeonSettlement();
            That(enabled?.BossKey == boss.ObjectKey, "罪恶用实际目标key开放结算");
            f.Session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
            byte[] result = DungeonSettlementProtocolTests.Report(f.Read().Id, 99);
            That(DungeonSettlementRequests.TryParsePlayResult(result, out var parsed), "罪恶使用正式46解析");
            var settled = f.Session.SettleFormalDungeon(parsed!.ActorId, parsed.RankPoint, parsed.MaximumRankPoint, result, dungeons, clears, growth);
            That(settled is { Outcome: DungeonRunOutcome.Accepted, Receipt: not null }, "罪恶结算入库");
            before = f.Read();
            That(f.Session.SettleFormalDungeon(parsed.ActorId, parsed.RankPoint, parsed.MaximumRankPoint, result, dungeons, clears, growth).Outcome == DungeonRunOutcome.Duplicate &&
                QuestCompletionRepositoryTests.Same(before, f.Read()), "罪恶重复46不重复发奖");
            That(f.Session.ReturnFromFormalDungeon() is not null &&
                f.Session.ObserveFormalDungeonScriptedDeath(report, kills, growth).Outcome == DungeonRunOutcome.Rejected,
                "罪恶结算后可返回，迟到目标报告不能跨实例提交");
            f.Session.ReturnToRoster();
            f.Session.Select(0, resumeTown: true);
            f.Session.MarkReady();
            That(f.Read().ActiveMainQuests.Single(row => row.QuestId == id) == accepted with { ConditionState = 0 } &&
                f.Session.Selected!.ActiveMainQuests.Single(row => row.QuestId == id) == accepted with { ConditionState = 0 },
                "罪恶重选恢复同一接取身份及已持久化的猎杀归零进度");
            That(f.Session.FinishQuest(id, ushort.MaxValue, quests, parameters, items.Items, items.GoldLimits, items.EpicPieces, growth).Receipt is not null &&
                f.Read().CompletedQuests.Contains(id), "罪恶重选后正常34交付");
        }
        Console.WriteLine($"PASS {Count - checks} 罪恶目标报告、结算返回及原接取持久化断言（合成输入，非实机）");
    }

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
