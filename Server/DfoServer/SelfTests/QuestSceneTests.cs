using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestSceneTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog? sharedQuests = null, SkillLearningCatalog? sharedSkills = null)
    {
        int checks = Count;
        var quests = sharedQuests ?? QuestCatalog.Load(pvf); var skills = sharedSkills ?? SkillLearningCatalog.Load(pvf, DfoServer.GameWorld.Items.PremiumContractCatalog.Load(pvf).ExpertSkillLevels);
        var growth = CharacterGrowthCatalog.Load(pvf,skills); var parameters = QuestRewardParameterCatalog.Load(pvf);
        var dungeons = DungeonCatalog.Load(pvf); var rooms = DungeonRoomCatalog.Load(pvf); var kills = DungeonKillExperienceCatalog.Load(pvf);
        foreach (var mode in new[] { "normal","lost","completedParent" })
        {
            bool lost = mode == "lost";
            using var f = new Fixture(towns,characters,skills,growth,quests,lost);
            That(f.Session.LeaveRoom(), "正常剧情从赛丽亚房间回城");
            That(Change(3146,QuestLifecycleAction.Accept)?.Commit is not null && Change(3157,QuestLifecycleAction.Accept)?.Commit is not null,
                "真实Lv3父任务及首个隐藏子任务按城镇对话顺序接取");
            That(Change(3054,QuestLifecycleAction.Accept)?.Commit is null, "完成第一子任务前不能提前接取第二子任务");
            That(f.Session.Travel(new(38,2,1085,249,5,38,0,0)), "真实Lv3剧情进入资源中的副本入口");
            var opening = f.Session.EnterFormalDungeon(3,3146,0,dungeons,rooms);
            That(opening is not null && opening.Entry.RouteQuestId == 3146 && opening.Snapshot.CurrentMap.Id == 76131,
                "父任务自己的quest connection选择真实六房路线");
            var loaded = f.Session.CompleteFormalDungeonLoading()!;
            var before = f.Read(); var online = f.Session.Selected!;
            That(Observe(13099,false) is null && Observe(13098,true) is null && Same(before,f.Read()),
                "校验失败或不存在的物件不推进任务");
            if (!lost)
            {
                f.Sql("CREATE TRIGGER scene_abort AFTER INSERT ON character_quest_lifecycle_receipts WHEN NEW.scene_event IS NOT NULL BEGIN SELECT RAISE(ABORT,'scene rollback'); END");
                TestCheck.Reject<SqliteException>(() => Observe(13099,true), "场景事件末段SQL失败");
                That(Same(before,f.Read()) && ReferenceEquals(online,f.Session.Selected), "被动物件进度和场景回执一起回滚");
                f.Sql("DROP TRIGGER scene_abort");
            }
            else TestCheck.Reject<IOException>(() => Observe(13099,true), "场景事件已保存但返回丢失");
            var progress = Observe(13099,true)!;
            That(progress.Commit is { ProjectionCurrent: true } && progress.Commit.Duplicate == lost &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 3157).ConditionState == 0 &&
                f.Read().Learning.Experience == before.Learning.Experience && f.Read().Inventory.Gold == before.Inventory.Gold &&
                Observe(13099,true) is null && f.Session.CanPublishQuestLifecycle(progress),
                "真实唯一被动物件只推进一次任务；结果丢失复用冻结计划，无击杀经验和金币");
            var child = Finish(3157);
            That(child.Receipt is { Phase: SessionPhase.Dungeon } && child.Receipt.Commit is { ConditionType: 6, Rewards.Length: 0, ExperienceReward.Amount: 0 } &&
                f.Session.CanPublishQuestCompletion(child.Receipt) && f.Read().CompletedQuests.Contains(3157) && !f.Read().CompletedQuests.Contains(3146),
                "实际被动物件隐藏子任务在同一副本交付，零奖励完成不提前完成父任务");
            That(Change(3054,QuestLifecycleAction.Accept)?.Commit is not null && f.Session.Phase == SessionPhase.Dungeon &&
                f.Session.DungeonSnapshot!.Identity.InstanceId == loaded.Snapshot.Identity.InstanceId,
                "原生顺序入口在原父实例中接下一子任务，不改地图或复用旧接取");
            That(Change(3157,QuestLifecycleAction.Accept)?.Commit is null && Finish(3054).Outcome == QuestFinishOutcome.Rejected,
                "已完成子任务不能重接，清图子任务没有凭据不能领奖");
            FormalDungeonReceipt receipt = loaded;
            var path = new (byte X,byte Y)[] { (0,1),(1,1),(2,1),(3,1),(4,1) };
            foreach (var point in path)
            {
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray())
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                        "任务路线使用真实服务端死亡事实推进房间");
                That(f.Session.MoveFormalDungeon(point.X,point.Y) is not null, "任务路线按实际连接换房");
                receipt = f.Session.CompleteFormalDungeonLoading()!;
            }
            foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).OrderBy(row => row.SpawnType == 3).ToArray())
            {
                var result = f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth);
                That(result.Outcome == DungeonRunOutcome.Accepted, $"剧情Boss房提交真实死亡与任务清图：{monster.MonsterId}/{monster.ObjectKey}/{result.Outcome}");
            }
            That(f.Session.ReadClearedQuest(3054)?.Clear is { MapId: 76136 } && f.Session.Phase == SessionPhase.Settlement,
                "中途新接子任务获得同实例末次死亡的持久清图凭据");
            // 复现da：清图后没有33/34，断线回城时仍保留本次接取的真实清图凭据。
            That(f.Session.ReturnFromFormalDungeon() is not null,"零奖励清图子任务未交付时可正常回城");
            if (mode == "completedParent")
            {
                f.Sql("INSERT INTO character_completed_quests(character_id,quest_id,daily_period,answer) SELECT id,3146,0,NULL FROM characters; DELETE FROM character_active_main_quests WHERE quest_id=3146");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                var orphanBefore = f.Read();
                var recovered = f.Session.FinishReadyQuestChildren(quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
                var orphanAfter = f.Read();
                That(recovered?.Commit is { ConditionType: 4, Clear: null, ExperienceReward.Amount: 0, GoldAmount: 0, Consumed.Length: 0 } &&
                    orphanAfter.CompletedQuests.Contains(3054) && orphanAfter.CompletedQuests.Contains(3146) &&
                    orphanAfter.ActiveMainQuests.Length == 0 && Same(orphanBefore with { Revision = orphanAfter.Revision,
                        ActiveMainQuests = orphanAfter.ActiveMainQuests, CompletedQuests = orphanAfter.CompletedQuests },orphanAfter),
                    "实际持久清图已存在时仍只归档已完成父任务的终末子项，保留死亡经验与全部资产");
                continue;
            }
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            var last = f.Session.FinishReadyQuestChildren(quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(last?.Commit is { Rewards.Length: 0, ExperienceReward.Amount: 0 } &&
                f.Read().ActiveMainQuests is [{ QuestId: 3146, ConditionState: 0 }], "实际两个子任务真实完成后父任务同事务归零");
            var parent = Finish(3146);
            if (parent.Outcome != QuestFinishOutcome.Committed)
            {
                var diagnostic = QuestCompletionPlan.Prepare(f.Session.Selected!,quests.Get(3146),ushort.MaxValue,quests,parameters,
                    items.Items,items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow);
                throw new InvalidOperationException($"父任务交付拒绝：{parent.Outcome}，{diagnostic.Reason}");
            }
            That(parent.Receipt?.Commit is { ConditionType: 44 } && f.Read().CompletedQuests.Contains(3146) &&
                f.Read().ActiveMainQuests.Length == 0 && Finish(3146).Outcome == QuestFinishOutcome.AlreadyCompleted,
                "资源场景对话交付父任务仍经C2S34原子发自身奖励，只能领取一次");
            That(f.Session.Phase == SessionPhase.Town, "父任务按真实子完成记录在城镇交付，不额外要求副本结算或常驻剧情NPC");
            var side = quests.Get(3147) with { Id = 29900, Grade = "[side story]" };
            var mixed = new QuestCatalog(quests.All.Append(side).ToDictionary(row => row.Id));
            var continuing = f.Read() with { ActiveMainQuests = [new(0,3147,1) { AcceptanceId = Guid.NewGuid() },
                new(1,29900,1) { AcceptanceId = Guid.NewGuid() }] };
            var successor = DungeonQuestContinuationRules.Resolve(continuing,opening!.Entry,mixed,dungeons,[6],out _);
            That(successor is { Quest.QuestId: 3147, DungeonId: 6 },"相同前置的已接取支线不与真实epic主线争抢转场");
            That(DungeonQuestContinuationRules.Resolve(continuing with { ActiveMainQuests = [continuing.ActiveMainQuests[1]] },
                opening.Entry,mixed,dungeons,[6],out _) is null,"只有已接取支线时不能将其冒充下一主线");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(f.Session.Selected!,f.Read()) && f.Read().CompletedQuests.Contains(3054), "整条实际Lv3父子链重登完整恢复");

            QuestLifecycleReceipt? Change(ushort id,QuestLifecycleAction action) => f.Session.ChangeQuest(id,action,quests,items.Items,items.GoldLimits,items.EpicPieces);
            QuestLifecycleReceipt? Observe(uint id,bool checksum) => f.Session.ObserveFormalPassiveDeath(id,checksum,new string('B',64),rooms);
            QuestFinishResult Finish(ushort id) => f.Session.FinishQuest(id,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
        }
        VerifyPassiveSettlement(towns, characters, items, quests, skills, growth, dungeons, rooms, kills);
        Console.WriteLine($"PASS {Count-checks} 实际父子剧情、物件结算、清图交付与恢复断言（非实机）");
    }

    private static void VerifyPassiveSettlement(TownCatalog towns, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, DungeonKillExperienceCatalog kills)
    {
        var area = towns.Areas.Values.First(row => row.Level <= 28 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 25));
        var done = new HashSet<int>(area.Quests) { 3204 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        foreach (string mode in new[] { "rollback", "lost", "stale", "independent" })
        {
            using var f = new Fixture(towns, characters, skills, growth, quests, mode is "lost" or "stale",
                28, done.ToArray(), position: position);
            That(f.Session.ChangeQuest(3205, QuestLifecycleAction.Accept, quests, items.Items, items.GoldLimits, items.EpicPieces)?.Commit is not null,
                "隔离真实入口正常接取3205");
            if (mode == "independent")
            {
                // 活动任务已有进度时，新的实例仍须提交本次迷宫物件事实，不能借scalar0提前结算。
                f.Sql("UPDATE character_active_main_quests SET condition_state=0 WHERE quest_id=3205");
                f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
            }
            var opening = f.Session.EnterFormalDungeon(25, 3205, 0, dungeons, rooms);
            That(opening is not null, "3205场景事务fixture已恢复实际入口区域并正常准入：" + mode);
            var path = QuestStoryRouteTests.Path(opening!.Entry.Maze.StaticTopology, opening.Entry.StartMap.Position, opening.Entry.BossMap.Position);
            FormalDungeonReceipt receipt = opening!;
            foreach (var point in path)
            {
                receipt = f.Session.CompleteFormalDungeonLoading()!;
                foreach (var monster in receipt.Monsters.Monsters.Where(monster => monster.IsBlocking))
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth).Outcome == DungeonRunOutcome.Accepted,
                        "实际3205路线由合成权威死亡通过原提交链清房");
                if (point == opening.Entry.BossMap.Position) break;
                var next = path[Array.IndexOf(path, point) + 1];
                That(f.Session.MoveFormalDungeon(checked((byte)next.X), checked((byte)next.Y)) is not null, "按实际门墙路线移动至3205终点");
            }
            var before = f.Read(); var online = f.Session.Selected!; var identity = f.Session.DungeonSnapshot!.Identity;
            That(receipt.Snapshot.CurrentMap.Id == 53493 && f.Session.Phase == SessionPhase.Dungeon &&
                f.Session.EnableFormalDungeonSettlement() is null, "物件提交前终点仍在Dungeon且无结算许可");
            if (mode == "rollback")
            {
                f.Sql("CREATE TRIGGER terminal_scene_abort AFTER INSERT ON character_quest_lifecycle_receipts WHEN NEW.scene_event IS NOT NULL BEGIN SELECT RAISE(ABORT,'terminal scene rollback'); END");
                TestCheck.Reject<SqliteException>(() => Observe(), "终点物件SQL末段回滚");
                f.Sql("DROP TRIGGER terminal_scene_abort");
                That(Same(before, f.Read()), "回滚保留完整角色资产和任务状态");
            }
            if (mode is "lost" or "stale") TestCheck.Reject<IOException>(() => Observe(), "终点物件提交返回丢失");
            if (mode != "independent")
                That(ReferenceEquals(f.Session.Selected, online) && f.Session.DungeonSnapshot!.Identity == identity &&
                    f.Session.Phase == SessionPhase.Dungeon && f.Session.EnableFormalDungeonSettlement() is null,
                    "未取得明确提交结果时不改变Run或提前许可结算");
            if (mode == "stale") f.Sql("UPDATE characters SET revision=revision+1");
            var progress = Observe()!;
            if (mode == "stale")
            {
                That(progress.Commit is { Duplicate: true, ProjectionCurrent: false } && !f.Session.CanPublishQuestLifecycle(progress) &&
                    ReferenceEquals(f.Session.Selected, online) && f.Session.Phase == SessionPhase.Dungeon && f.Session.DungeonSnapshot!.Identity == identity,
                    "已过期的持久回执不推进Run、不覆盖在线角色或发布成功");
                continue;
            }
            var enable = f.Session.EnableFormalDungeonSettlement();
            That(progress.Commit is { ProjectionCurrent: true } && progress.Commit.Duplicate == (mode == "lost") &&
                f.Session.CanPublishQuestLifecycle(progress) && progress.Phase == SessionPhase.Settlement &&
                progress.DungeonIdentity == f.Session.DungeonSnapshot!.Identity && progress.DungeonIdentity!.Epoch == identity.Epoch + 1 &&
                enable is { BossKey: null } && enable.Value.Receipt.Snapshot.CurrentMap.Id == 53493,
                "原操作恢复后回执绑定实际结算phase与epoch，空终点没有伪造Boss key");
            var committed = f.Read();
            That(Observe() is null && Same(committed, f.Read()) &&
                f.Session.ObserveFormalPassiveDeath(80458, true, new string('D', 64), rooms) is null,
                "同物件33重放或39镜像不再次推进revision或结算");
            f.Session.ConfirmFormalDungeonSettlementEnabled(enable!.Value.Receipt);
            That(f.Session.EnableFormalDungeonSettlement() is null, "成功发布后本实例不重复许可结算");
            QuestLifecycleReceipt? Observe() => f.Session.ObserveFormalPassiveTrigger(3205, new(0x10, 0, 0), new string('C', 64), rooms);
        }
    }

    internal sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(),"S5A9-quest-scene-"+Guid.NewGuid().ToString("N"));
        readonly string path;
        readonly AccountOwners owners = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public GameSession Session { get; }
        public Fixture(TownCatalog towns,CharacterCatalog characters,SkillLearningCatalog skills,CharacterGrowthCatalog growth,QuestCatalog quests,bool lost,
            byte level = 3, int[]? completed = null, bool loseGrowth = false, byte job = 0, Location? position = null)
        {
            path = Path.Combine(root,"game.db"); Repository = new(path,quests); Owner = owners.Acquire("scene-quest",() => { });
            var candidate = CharacterCreation.Prepare("SceneQuest",characters.Get(job),position ?? towns.Room,position ?? towns.InitialReturn,skills.Get);
            var learning = SkillPointLedger.Synchronize(job,0,level,candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(level) },skills);
            Repository.Create(Owner,candidate with { Level = level, CompletedQuests = completed ?? [3145,4873], InitialLearning = learning,
                AcceptedStoryLevel = position is null ? candidate.AcceptedStoryLevel : level });
            Session = new(lost || loseGrowth ? new LostResultRepository(Repository) { LoseSceneResult = lost, LoseCompletionResult = false, LoseGrowthResult = loseGrowth } : Repository,towns,Owner,questCatalog: quests);
            Session.Select(0, resumeTown: position is not null); Session.MarkReady();
        }
        public Character Read() => Repository.LoadCharacter(Owner,0);
        public Dictionary<(byte,byte),(byte,byte)> TravelPaths(TownCatalog towns)
        {
            if (towns.IsRoom(Session.Selected!.Position)) That(Session.LeaveRoom(),"选角后从真实房间返回城镇");
            var start = (Session.Selected!.Position.Town,Session.Selected.Position.Area);
            var pending = new Queue<(byte,byte)>(); pending.Enqueue(start);
            var previous = new Dictionary<(byte,byte),(byte,byte)> { [start] = start };
            var done = QuestEligibility.Completed(Session.Selected);
            while (pending.TryDequeue(out var next))
                foreach (var link in towns.Areas[next].Map(done).Links.Where(link => link.Gate.Door && link.Town != 255))
                {
                    var destination = (link.Town,link.Area);
                    if (!previous.ContainsKey(destination) && towns.Admission(new(next.Item1,next.Item2,0,0),
                        new(link.Town,link.Area,0,0),Session.Selected.Level,done) == "ready")
                    { previous.Add(destination,next); pending.Enqueue(destination); }
                }
            return previous;
        }
        public void TravelTo(TownCatalog towns,Location position)
        {
            var previous = TravelPaths(towns);
            var start = (Session.Selected!.Position.Town,Session.Selected.Position.Area); var target = (position.Town,position.Area);
            var done = QuestEligibility.Completed(Session.Selected);
            That(previous.ContainsKey(target),"实际PVF城镇连接可达任务场景");
            var steps = new List<(byte,byte)>();
            for (var next = target; next != start; next = previous[next]) steps.Add(next);
            steps.Reverse();
            foreach (var next in steps)
            {
                var bounds = towns.Areas[next].Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
                var source = Session.Selected.Position;
                That(Session.Travel(new(next.Item1,next.Item2,checked((ushort)(bounds.X + bounds.Width / 2)),
                    checked((ushort)(bounds.Y + bounds.Height / 2)),0,source.Town,source.Area,0)),"按真实连接到达下一城镇区域");
            }
            That(Session.Move(new(position.X,position.Y,0,0,0,0)),"任务测试落点符合城镇可行走资源");
        }
        public void Sql(string sql) { using var db=new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open(); using var c=db.CreateCommand(); c.CommandText=sql; c.ExecuteNonQuery(); }
        public void Dispose() { Session.End(); Owner.Dispose(); Directory.Delete(root,true); }
    }
}
