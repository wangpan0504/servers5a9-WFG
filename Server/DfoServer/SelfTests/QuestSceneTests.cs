using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
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
        VerifyRoomProgress(towns,characters,items,quests,skills,growth,dungeons,rooms,kills,parameters);
        VerifyTerminalLayerReentry(towns,characters,items,quests,skills,growth,dungeons,rooms,kills);
        QuestPassiveClearTests.Run(towns,characters,items,quests,skills,growth,dungeons,rooms,kills,
            DungeonClearExperienceCatalog.Load(pvf),parameters);
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

    // 使用真实父3525/子3560与92247资源；死亡合成，不能替代客户端演出验收。
    private static void VerifyRoomProgress(TownCatalog towns,CharacterCatalog characters,ItemResources items,
        QuestCatalog quests,SkillLearningCatalog skills,CharacterGrowthCatalog growth,DungeonCatalog dungeons,
        DungeonRoomCatalog rooms,DungeonKillExperienceCatalog kills,QuestRewardParameterCatalog parameters)
    {
        // 独立合成支线仅用于核对同一真实目标房的多接取原子性，不冒充PVF中另一个任务。
        var siblingSource = quests.Get(3560);
        var sibling = siblingSource with { Id = 29900,MainQuest = null,Grade = "[side]",
            Definition = siblingSource.Definition! with { ExposedByNpc = true } };
        quests = new(quests.All.Append(sibling).ToDictionary(row => row.Id));
        var area = towns.Areas.Values.First(row => row.Level <= 65 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 82));
        var done = new HashSet<int>(area.Quests) { 3145,4873,3524,3558,3559 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        foreach (bool lost in new[] { false,true })
        {
            var clock = lost ? new ConsumableUseSessionTests.FakeClock() : null;
            long? contractExpiry = clock?.GetUtcNow().AddSeconds(1).ToUnixTimeSeconds();
            using var f = new Fixture(towns,characters,skills,growth,quests,false,65,done.ToArray(),loseGrowth: lost,
                position: position,growthLossCondition: prepared => prepared.Result.RoomClearFact is not null,
                contracts: lost ? items.Contracts : null,timeProvider: clock,growthContractExpiresAt: contractExpiry);
            int expiredKills = 0;
            That(Change(3525)?.Commit is not null && f.Read().ActiveMainQuests.Single().ConditionState == 1,
                "真实父任务按已完成两个会面子任务进入最后清图阶段");
            var opening = f.Session.EnterFormalDungeon(82,3525,0,dungeons,rooms)!;
            That(opening is not null && opening.Entry.QuestClearMap is { Id: 92247,Position.X: 2,Position.Y: 1 } &&
                opening.Entry.BossMap.Id == 92251 && opening.Entry.BaseBossMap == opening.Entry.BossMap &&
                !f.Read().ActiveMainQuests.Any(row => row.QuestId == 3560),"父请求借真实子布局，不造接取或替换原Boss");
            var receipt = f.Session.CompleteFormalDungeonLoading()!;
            That(Change(3560)?.Commit is not null,"加载后真实31接取清图子任务并绑定本Run");
            That(Change(29900)?.Commit is not null,"同图合成支线以独立接取身份绑定原Run");
            var acceptance = f.Read().ActiveMainQuests.Single(row => row.QuestId == 3560).AcceptanceId;
            var entry = opening!.Entry;
            var target = entry.QuestClearMap!;
            var path = QuestStoryRouteTests.Path(entry.Maze.StaticTopology,entry.StartMap.Position,target.Position);
            foreach (var point in path.Skip(1))
            {
                KillRoom();
                That(f.Session.MoveFormalDungeon(checked((byte)point.X),checked((byte)point.Y)) is not null,"真实门墙连接通往中途任务房");
                receipt = f.Session.CompleteFormalDungeonLoading()!;
            }
            var enemies = receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray();
            That(enemies.Length == 6 && enemies.All(row => row.MonsterId == 61801) &&
                receipt.Monsters.Monsters.Any(row => row.MonsterId == 10448 && row.IsFriendly && !row.IsBlocking),
                "任务房保留真实六敌与友方APC");
            foreach (var monster in enemies[..^1]) Kill(monster);
            That(f.Session.ReadClearedQuest(3560) is null && f.Session.EnableFormalDungeonSettlement() is null,
                "少一敌人不能达成清图或提前许可结算");
            var observation = FormalDungeonSessionTests.Observation(receipt,enemies[^1]);
            var scripted = new NativeDungeonScriptedDeathObservation(enemies[^1].ObjectKey,0,true,new string('D',64));
            var before = f.Read(); var online = f.Session.Selected; var originalIdentity = f.Session.DungeonSnapshot!.Identity;
            uint basis = ExpectedExperience(enemies[^1]);
            uint bonus = lost ? checked((uint)((ulong)basis * (uint)items.Contracts.Growth!.ExperiencePercent / 100)) : 0;
            Character? savedBeforeRecovery = null;
            QuestClearReceipt? savedClear = null;
            if (lost)
            {
                TestCheck.Reject<IOException>(() => SubmitLast(),"合成脚本39的目标房事务提交后返回丢失");
                That(f.Read().ActiveMainQuests.Single(row => row.QuestId == 3560).ConditionState == 0 &&
                    ReferenceEquals(f.Session.Selected,online) && f.Session.DungeonSnapshot.Identity == originalIdentity &&
                    f.Session.ReadClearedQuest(3560) is null,"数据库已达成但未确认Run，不提前读取或安装Selected");
                savedBeforeRecovery = f.Read();
                savedClear = f.Repository.LoadQuestClear(f.Owner,savedBeforeRecovery,3560)!;
                That(bonus > 0 && savedBeforeRecovery.Learning.Experience == before.Learning.Experience + basis + bonus &&
                    savedBeforeRecovery.Revision == before.Revision + 1 &&
                    f.GrowthReceipt(savedClear.EventId) == (1L,(long)basis + bonus),
                    "返回丢失前已按有效账户契约提交原死亡经验与唯一清图事件");
                // 可控时钟推进到精确到期；同一死亡恢复必须保留到期前的原奖励。
                clock!.Advance(TimeSpan.FromSeconds(1));
                That(clock.GetUtcNow().ToUnixTimeSeconds() == contractExpiry &&
                    before.Inventory.AccountWallet.ContractExpiry(84) == contractExpiry,
                    "原角色账户真实期限不变，仅推进测试时钟至契约到期");
            }
            else
            {
                f.Sql("CREATE TRIGGER room_clear_abort AFTER INSERT ON character_quest_clear_receipts BEGIN SELECT RAISE(ABORT,'room clear rollback'); END");
                TestCheck.Reject<SqliteException>(() => SubmitLast(),"中途清图末段SQL失败");
                That(Same(before,f.Read()) && ReferenceEquals(f.Session.Selected,online) &&
                    f.Session.DungeonSnapshot.Identity == originalIdentity,"回滚保留全部资产、任务与原epoch候选");
                f.Sql("DROP TRIGGER room_clear_abort");
            }
            var result = SubmitLast();
            var committed = f.Read(); var clear = f.Session.ReadClearedQuest(3560)!;
            That(result.Outcome == DungeonRunOutcome.Accepted && result.ClearFact is null && f.Session.Phase == SessionPhase.Dungeon &&
                f.Session.DungeonSnapshot!.Identity.Epoch == originalIdentity.Epoch+1 && committed.Revision == before.Revision+1 &&
                clear.Clear is { MapId: 92247,SourceType: QuestClearSourceType.MonsterDeath } && clear.Clear.AcceptanceId == acceptance &&
                f.Session.CanPublishQuestCondition(clear) && f.Session.EnableFormalDungeonSettlement() is null &&
                result.Receipt!.Snapshot.LivingMonsters.Contains(receipt.Monsters.Monsters.Single(row => row.MonsterId == 10448).ObjectKey),
                "原成长事务精确恢复并确认RoomClear；保留友方且不生成终点奖励或结算许可");
            if (lost)
                That(result.Reward is { } reward && reward.Amount == basis + bonus && reward.GrowthContractExperience == bonus &&
                    result.GainedExperience == basis + bonus && Same(savedBeforeRecovery!,committed) &&
                    clear.Clear.EventId == savedClear!.EventId && f.GrowthReceipt(clear.Clear.EventId) == (1L,(long)basis + bonus),
                    "到期后精确恢复沿用原死亡额度和契约份额，不重写revision、清图身份或再发经验");
            That(f.Session.ReadClearedQuest(29900) is { } siblingClear && siblingClear.Clear.EventId == clear.Clear.EventId &&
                siblingClear.Clear.AcceptanceId != clear.Clear.AcceptanceId &&
                committed.ActiveMainQuests.Single(row => row.QuestId == 29900).ConditionState == 0,
                "同次实际清房原子推进全部合法匹配接取，凭据保持各自AcceptanceId");
            That(SubmitLast().Outcome == DungeonRunOutcome.Duplicate && Same(committed,f.Read()),
                "确认后的旧epoch死亡仅重复忽略，不二次授予经验或进度");
            if (lost) That(f.GrowthReceipt(clear.Clear.EventId) == (1L,(long)basis + bonus),
                "确认后的重复死亡不增加原事件收据或改写冻结额度");
            var toBoss = QuestStoryRouteTests.Path(entry.Maze.StaticTopology,target.Position,entry.BossMap.Position);
            foreach (var point in toBoss.Skip(1))
            {
                KillRoom();
                That(f.Session.MoveFormalDungeon(checked((byte)point.X),checked((byte)point.Y)) is not null,"清图后继续原Boss路线");
                That(!f.Session.CanPublishQuestCondition(clear),"过房使旧发布快照失效");
                receipt = f.Session.CompleteFormalDungeonLoading()!;
                That(f.Session.ReadClearedQuest(3560)?.Clear == clear.Clear,"后续房间仍读取本Run具体已提交清图事实");
            }
            KillRoom();
            if (lost) That(expiredKills > 0,"恢复后确有新死亡经过到期契约分支，不仅检查旧事件重放");
            That(f.Session.Phase == SessionPhase.Settlement && f.Session.ReadClearedQuest(3560)?.Clear == clear.Clear &&
                f.Session.EnableFormalDungeonSettlement() is not null,"真正Boss才产生结算，中途任务凭据不丢失");
            That(f.Session.ReturnFromFormalDungeon() is not null,"已达成子任务可以保留凭据回城");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(f.Session.ReadClearedQuest(3560)?.Clear == clear.Clear,"重登从原growth外键恢复中途清图资格");
            var finished = f.Session.FinishQuest(3560,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(finished.Outcome == QuestFinishOutcome.Committed && f.Read().CompletedQuests.Contains(3560) &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 3525).ConditionState == 0,"子任务真实交付后按原顺序推进父任务");

            QuestLifecycleReceipt? Change(ushort id) => f.Session.ChangeQuest(id,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces);
            FormalDungeonDeathResult SubmitLast() => lost ? f.Session.ObserveFormalDungeonScriptedDeath(scripted,kills,growth) :
                f.Session.ObserveFormalDungeonDeath(observation,kills,growth);
            uint ExpectedExperience(DungeonPlannedMonster monster) => DungeonKillExperiencePolicy.Calculate(
                kills.GetExperience(monster.CandidateLevel),entry.ExperienceWeight,kills.GetDifficultyRate(entry.Difficulty),
                kills.GetMonsterRate(monster.ExperienceType,monster.AiCharacter is null && entry.NamedMonsters.Contains(monster.MonsterId)),
                f.Session.Selected!.Level,monster.CandidateLevel);
            void Kill(DungeonPlannedMonster monster)
            {
                uint expected = ExpectedExperience(monster);
                bool expired = clock is not null && clock.GetUtcNow().ToUnixTimeSeconds() >= contractExpiry!.Value;
                var death = f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth);
                That(death.Outcome == DungeonRunOutcome.Accepted,"合成已登记敌对死亡沿原成长事务提交");
                if (expired)
                {
                    That(death.Reward is { GrowthContractExperience: 0 } reward && reward.Amount == expected &&
                        death.GainedExperience == expected,"原奖励恢复后新的合法死亡按已到期账户资格发基础经验，不沿用旧契约加成");
                    expiredKills++;
                }
            }
            void KillRoom()
            {
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).OrderBy(row => row.IsBoss).ToArray())
                    if (f.Session.DungeonSnapshot!.LivingMonsters.Contains(monster.ObjectKey)) Kill(monster);
            }
        }
    }

    private static void VerifyTerminalLayerReentry(TownCatalog towns, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, DungeonKillExperienceCatalog kills)
    {
        var area = towns.Areas.Values.First(row => row.Level <= 92 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 291100319));
        var done = new HashSet<int>(area.Quests) { 6031 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        foreach (int id in area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0 && !gate.Quests.Contains(6032)).Quests)
            done.Add(id);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        using var f = new Fixture(towns,characters,skills,growth,quests,false,92,done.ToArray(),position:position);
        That(f.Session.ChangeQuest(6032,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
            "真实搏击俱乐部入口按前置接取末层清图任务");
        var original = f.Read().ActiveMainQuests.Single(row => row.QuestId == 6032);
        // 裸scalar0没有任何死亡/场景持久凭据，不能通过普通0入口冒充已批准重入。
        f.Sql("UPDATE character_active_main_quests SET condition_state=0 WHERE quest_id=6032");
        Reselect();
        That(f.Session.EnterFormalDungeon(291100319,0,0,dungeons,rooms) is null,
            "真实Session普通0拒绝无持久清图凭据的裸state0");
        f.Sql("UPDATE character_active_main_quests SET condition_state=1 WHERE quest_id=6032");
        Reselect();
        var opening = f.Session.EnterFormalDungeon(291100319,0,0,dungeons,rooms);
        That(opening is { Entry.TerminalLayerMap: { Position.X: 2, Position.Y: 0, Id: 59914 } } &&
            opening.Entry.TerminalLayerQuestId == original.QuestId && opening.Entry.TerminalLayerQuestSlot == original.Slot &&
            opening.Entry.TerminalLayerAcceptanceId == original.AcceptanceId,
            "普通0冻结实际layout任务三元组，不把请求0当目标任务");
        var receipt = opening ?? throw new InvalidOperationException("末层任务实例未建立");
        var target = receipt.Entry.TerminalLayerMap!;
        ClearTerminal(receipt);
        var first = f.Session.ReadClearedQuest(6032)?.Clear;
        That(first is not null && first.AcceptanceId == original.AcceptanceId && f.Session.Phase == SessionPhase.Settlement,
            "合成可信死亡经真实Session和SQLite形成原接取持久凭据（非客户端验收）");
        That(f.Session.ReturnFromFormalDungeon() is not null,"未交付任务可沿原退出回城");
        Reselect();
        var repeated = f.Session.EnterFormalDungeon(291100319,0,0,dungeons,rooms);
        That(repeated is not null && repeated.Entry.TerminalLayerMap == target && repeated.Entry.TerminalLayerAcceptanceId == original.AcceptanceId &&
            f.Repository.LoadQuestClear(f.Owner,f.Session.Selected!,6032) == first &&
            f.Read().ActiveMainQuests.Single(row => row.QuestId == 6032).ConditionState == 0,
            "同接取已达成未34后普通0凭原持久账本重入，保留末层及首清图凭据");
        ClearTerminal(repeated!);
        That(f.Session.Phase == SessionPhase.Settlement && f.Repository.LoadQuestClear(f.Owner,f.Session.Selected!,6032) == first &&
            !f.Read().CompletedQuests.Contains(6032), "再次真实末层死亡不覆盖首清图凭据或代替34领奖");

        void ClearTerminal(FormalDungeonReceipt entered)
        {
            var path = QuestStoryRouteTests.Path(entered.Entry.Maze.StaticTopology,entered.Entry.StartMap.Position,target.Position);
            for (int index = 0; index < path.Length; index++)
            {
                var loaded = f.Session.CompleteFormalDungeonLoading()!;
                Clear(loaded);
                if (index == path.Length-1) break;
                That(f.Session.MoveFormalDungeon(checked((byte)path[index+1].X),checked((byte)path[index+1].Y)) is not null,
                    "真实路线按原连接到达末层入口");
            }
            That(f.Session.Phase == SessionPhase.Dungeon &&
                f.Session.MoveFormalDungeon(checked((byte)target.Position.X),checked((byte)target.Position.Y),1) is not null,
                "中途清房不结算，按原kind1进入真实末层");
            Clear(f.Session.CompleteFormalDungeonLoading()!);
        }

        void Clear(FormalDungeonReceipt loaded)
        {
            foreach (var monster in loaded.Monsters.Monsters.Where(row => row.CanReportDeath).OrderBy(row => row.IsBoss).ToArray())
                That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded,monster),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                    "末层重入回归复用合成死亡与原成长提交入口");
        }
        void Reselect() { f.Session.ReturnToRoster(); f.Session.Select(0,resumeTown:true); f.Session.MarkReady(); }
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
            byte level = 3, int[]? completed = null, bool loseGrowth = false, byte job = 0, Location? position = null,
            Func<PreparedDungeonDeath,bool>? growthLossCondition = null,PremiumContractCatalog? contracts = null,
            TimeProvider? timeProvider = null,long? growthContractExpiresAt = null)
        {
            path = Path.Combine(root,"game.db"); Repository = new(path,quests); Owner = owners.Acquire("scene-quest",() => { });
            var candidate = CharacterCreation.Prepare("SceneQuest",characters.Get(job),position ?? towns.Room,position ?? towns.InitialReturn,skills.Get);
            var learning = SkillPointLedger.Synchronize(job,0,level,candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(level) },skills);
            var created = Repository.Create(Owner,candidate with { Level = level, CompletedQuests = completed ?? [3145,4873], InitialLearning = learning,
                AcceptedStoryLevel = position is null ? candidate.AcceptedStoryLevel : level });
            if (growthContractExpiresAt is { } expiry)
                Repository.CommitInventory(Owner,created,new(created.Inventory,created.Inventory with
                    { AccountWallet = created.Inventory.AccountWallet.SetContractExpiry(84,expiry) },"test_growth_contract"));
            Session = new(lost || loseGrowth ? new LostResultRepository(Repository) { LoseSceneResult = lost, LoseCompletionResult = false,
                LoseGrowthResult = loseGrowth,GrowthLossCondition = growthLossCondition } : Repository,towns,Owner,questCatalog: quests,
                contracts: contracts,timeProvider: timeProvider);
            Session.Select(0, resumeTown: position is not null); Session.MarkReady();
        }
        public Character Read() => Repository.LoadCharacter(Owner,0);
        public (long Count,long Amount) GrowthReceipt(Guid eventId)
        {
            using var db = new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT count(*),coalesce(max(amount),0) FROM character_dungeon_growth_receipts WHERE character_id=$id AND event_id=$event";
            command.Parameters.AddWithValue("$id",Read().Id); command.Parameters.AddWithValue("$event",eventId.ToString("N"));
            using var reader = command.ExecuteReader(); reader.Read();
            return (reader.GetInt64(0),reader.GetInt64(1));
        }
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
