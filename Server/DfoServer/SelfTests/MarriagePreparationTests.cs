using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Marriage;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Marriage;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MarriagePreparationTests
{
    public static void RunCore()
    {
        int checks = Count;
        var first = QuestEligibilityTests.Quest(20737) with
            { Grade = "[wedding]", Condition = "[condition under clear]", SubType = 22, IntData = [-1, -1, 5], MinimumLevel = 30 };
        var second = QuestEligibilityTests.Quest(20738) with
            { Grade = "[wedding]", Condition = "[seeking]", IntData = [10002610, 1], MinimumLevel = 30,
                Definition = new QuestDefinition([]) { Job = "[all]", PrerequisiteGroups = [[20737]] } };
        var quests = new QuestCatalog(new[] { first, second }.ToDictionary(quest => quest.Id));
        var relation = new MarriageRecord(Guid.NewGuid(), 1, 1, 1, 2, 1);
        var character = new Character(1, 0, "Bride", 0, new(1, 0, 50, 50), null) { Level = 30, Marriage = relation };
        That(QuestDungeonClearProgress.Supports(first) && QuestDungeonClearProgress.Initial(first) == 5 &&
            QuestDungeonClearProgress.ValidScalar(first, 0) && QuestDungeonClearProgress.ValidScalar(first, 5) &&
            !QuestDungeonClearProgress.ValidScalar(first, 6), "准备任务的五次剩余量来自资源声明");
        That(QuestEligibility.Evaluate(character, first, quests) is { Eligible: true, Visible: true } &&
            !QuestEligibility.Evaluate(character, second, quests).Eligible &&
            QuestEligibility.Evaluate(character with { CompletedQuests = [20737] }, second, quests).Eligible,
            "当前婚约可见准备任务，第二步仍要求第一步真实完成");
        foreach (var invalid in new[] { character with { Marriage = null }, character with { Marriage = relation with { State = 0 } },
            character with { Marriage = relation with { State = 4 } }, character with { Id = 3 } })
            That(!QuestEligibility.Evaluate(invalid, first, quests).Eligible, "无婚约、已结束、已婚及非成员不能取得准备资格");
        foreach (var invalid in new[] { first with { Id = 123 }, first with { Grade = "[side]" }, first with { SubType = 23 },
            first with { IntData = [-1, 0, 5] }, first with { IntData = [-1, -1, 0] } })
            That(!QuestMarriageProgress.Preparation(invalid), "不扩大未知任务、分类、子型或选择器");
        foreach (var invalid in new[] { first with { Id = 20738 }, second with { Id = 20739 },
            second with { IntData = [10002610, 0] } })
            That(!QuestMarriageProgress.Supports(invalid), "统一缺省难度不放宽婚约任务形状及材料数量校验");
        var noMap = first with { Definition = new([new("appear map", [new(0, -1, null), new(0, -1, null),
            new(0, -1, null), new(0, -1, null)], [])]) };
        var unknownMap = noMap with { Definition = new([new("appear map", [new(0, -1, null), new(0, -1, null),
            new(0, 0, null), new(0, -1, null)], [])]) };
        That(QuestCoverage.Unconfirmed(noMap).Length == 0 && QuestCoverage.Unconfirmed(unknownMap).Contains("appear map") &&
            QuestCoverage.Unconfirmed(noMap with { Id = 123 }).Contains("appear map"),
            "仅两份结婚任务的四个负一空地图声明得到识别，其它映射仍保留未知");
        Guid instance = Guid.NewGuid();
        var witness = new QuestMarriagePartyClear(instance, relation.Id, 1, 2);
        That(QuestMarriageProgress.Matches(relation, witness, instance) &&
            !QuestMarriageProgress.Matches(relation, witness with { SecondId = 3 }, instance) &&
            !QuestMarriageProgress.Matches(relation, witness with { MarriageId = Guid.NewGuid() }, instance) &&
            !QuestMarriageProgress.Matches(relation, witness, Guid.NewGuid()), "冻结队伍严格绑定原双方、婚约和共享实例");
        That(QuestMarriageProgress.EventKey(instance, 0) != QuestMarriageProgress.EventKey(instance, 1) &&
            QuestMarriageProgress.EventKey(instance, 1) != QuestMarriageProgress.EventKey(instance, 2) &&
            QuestMarriageProgress.EventKey(instance, 1) == QuestMarriageProgress.EventKey(instance, 1),
            "同一实例的共同次数与双方胶囊键独立且重放稳定");
        Console.WriteLine($"PASS {Count - checks} marriage preparation domain assertions");
    }

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items)
    {
        int checks = Count;
        string directory = Path.Combine(Path.GetTempPath(), "s5a9-marriage-preparation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "game.db");
        var owners = new AccountOwners();
        using var firstOwner = owners.Acquire("marriage-clear-first", () => { });
        using var secondOwner = owners.Acquire("marriage-clear-second", () => { });
        try
        {
            var auth = new AuthenticationService(new AuthenticationRepository(path));
            auth.Create(firstOwner.Account, "fixture-password"); auth.Create(secondOwner.Account, "fixture-password");
            var quests = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
            var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
            var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning);
            var catalog = MarriageCatalog.Load(pvf);
            var repository = new CharacterRepository(path, quests) { MarriageResources = catalog };
            Character first = Create(firstOwner, "PrepBride"), second = Create(secondOwner, "PrepGroom");
            var relation = new MarriageRecord(Guid.NewGuid(), 1, 1, first.Id, second.Id, 1)
            {
                CreatedAt = 1000,
                Members = ImmutableDictionary<ushort, MarriageMemberState>.Empty.Add(first.Id, new()).Add(second.Id, new())
            };
            var engaged = repository.CommitMarriageTogether(firstOwner, secondOwner, first, second, Noop(first), Noop(second), null, relation);
            first = Accept(engaged.First, firstOwner); second = Accept(engaged.Second!, secondOwner);
            Guid instance = Guid.NewGuid();
            var frozen = new QuestMarriagePartyClear(instance, relation.Id, first.Id, second.Id);
            var firstClear = Prepare(first, instance, frozen);
            Sql("CREATE TRIGGER fail_marriage_clear AFTER UPDATE ON character_active_main_quests WHEN NEW.quest_id=20737 BEGIN SELECT RAISE(ABORT,'marriage clear rollback'); END");
            var before = first;
            Reject<SqliteException>(() => Commit(firstOwner, before, firstClear), "准备次数末段失败须回滚经验、关系及任务");
            That(repository.FindMarriage(relation.Id) is { Revision: 1, PrepareClears: 0 } &&
                Scalar("SELECT count(*) FROM character_dungeon_growth_receipts") == 0 &&
                repository.LoadCharacter(firstOwner, first.Slot).ActiveMainQuests.Single().ConditionState == 5,
                "清图事务失败没有单独累计婚约或任务");
            Sql("DROP TRIGGER fail_marriage_clear");
            first = Commit(firstOwner, before, firstClear).Character;
            var replay = Commit(firstOwner, before, firstClear);
            That(replay.Duplicate && first.ActiveMainQuests.Single().ConditionState == 4 &&
                repository.FindMarriage(relation.Id)?.PrepareClears == 1, "原末次死亡重放不二次减数或累计婚约");
            firstClear.Run.ConfirmCommittedDeath(firstClear.Pending);
            var secondClear = Prepare(second, instance, frozen);
            second = Commit(secondOwner, second, secondClear).Character;
            That(second.ActiveMainQuests.Single().ConditionState == 4 && repository.FindMarriage(relation.Id)?.PrepareClears == 1,
                "共享清图分别推进双方原接取，但共同准备计数只记一次");
            secondClear.Run.ConfirmCommittedDeath(secondClear.Pending);
            var solo = Prepare(first, Guid.NewGuid(), null);
            first = Commit(firstOwner, first, solo).Character;
            That(first.ActiveMainQuests.Single().ConditionState == 4 && repository.FindMarriage(relation.Id)?.PrepareClears == 1,
                "单人可信通关仍不能推进双方准备任务");
            solo.Run.ConfirmCommittedDeath(solo.Pending);
            for (int count = 0; count < 4; count++)
            {
                instance = Guid.NewGuid(); frozen = frozen with { SharedInstanceId = instance };
                firstClear = Prepare(first, instance, frozen);
                first = Commit(firstOwner, first, firstClear).Character;
                firstClear.Run.ConfirmCommittedDeath(firstClear.Pending);
            }
            That(first.ActiveMainQuests.Single().ConditionState == 0 && repository.LoadQuestClear(firstOwner, first, 20737) is not null &&
                repository.FindMarriage(relation.Id)?.PrepareClears == 5 && !first.CompletedQuests.Contains(20737),
                "第五次真实两人清图形成原任务领奖凭据，尚未领取奖励或伪写完成位");
            var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
            var preparation = quests.Get(20737);
            That(preparation.Difficulty is null, "正式结婚准备第一步资源省略难度声明");
            var completion = QuestCompletionPlan.Prepare(first, preparation, ushort.MaxValue, quests, parameters,
                items.Items, items.GoldLimits, items.EpicPieces, growth, DateTimeOffset.UtcNow,
                repository.LoadQuestClear(firstOwner, first, 20737));
            That(completion is { Outcome: QuestCompletionOutcome.Accepted, Plan: not null },
                "五次通关后可规划正式任务完整领奖：" + completion.Reason);
            var award = repository.CommitQuestCompletion(firstOwner, completion.Plan!);
            var repeatedAward = repository.CommitQuestCompletion(firstOwner, completion.Plan!);
            first = repository.LoadCharacter(firstOwner, first.Slot);
            That(first.CompletedQuests.Contains(20737) && !first.ActiveMainQuests.Any(row => row.QuestId == 20737) &&
                first.Inventory.Items.Where(item => item.Core.ItemId == 10002616).Sum(item => item.Core.Count) == 1 &&
                award.ExperienceReward.DifficultyWeight == 100 && repeatedAward.Duplicate &&
                repeatedAward.Character.Learning.Experience == first.Learning.Experience,
                "完成位、单份稀有奖励及经验同事务落库，原领奖重放不重复到账");
            var secondPreparation = quests.Get(20738);
            That(secondPreparation is { Difficulty: null, Condition: "[seeking]", SubType: -1, IntData: [10002610, 1] },
                "结婚准备第二步保留真实省略难度的寻物声明");
            first = repository.CommitQuestLifecycle(firstOwner,QuestLifecyclePlan.Accept(first,secondPreparation,quests,
                items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow)).Character;
            var secondCompletion = QuestCompletionPlan.Prepare(first,secondPreparation,ushort.MaxValue,quests,parameters,
                items.Items,items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow);
            That(secondCompletion is { Outcome: QuestCompletionOutcome.Accepted, Plan: not null },
                "婚约、前置及真实材料齐备后第二步可规划领奖："+secondCompletion.Reason);
            var beforeSecond = first;
            Sql("CREATE TRIGGER fail_second_preparation AFTER INSERT ON character_quest_completion_receipts WHEN NEW.quest_id=20738 BEGIN SELECT RAISE(ABORT,'second preparation rollback'); END");
            Reject<SqliteException>(() => repository.CommitQuestCompletion(firstOwner,secondCompletion.Plan!),
                "第二步领奖末段失败");
            That(QuestCompletionRepositoryTests.Same(beforeSecond,repository.LoadCharacter(firstOwner,first.Slot)),
                "第二步失败不扣材料、不保存完成或奖励");
            Sql("DROP TRIGGER fail_second_preparation");
            var secondAward = repository.CommitQuestCompletion(firstOwner,secondCompletion.Plan!);
            var secondReplay = repository.CommitQuestCompletion(firstOwner,secondCompletion.Plan!);
            first = repository.LoadCharacter(firstOwner,first.Slot);
            That(first.CompletedQuests.Contains(20738) && first.ActiveMainQuests.All(row=>row.QuestId!=20738) &&
                secondAward.Consumed is [{ ItemId: 10002610, Count: 1 }] && secondAward.ExperienceReward.DifficultyWeight==100 &&
                secondReplay.Duplicate && QuestCompletionRepositoryTests.Same(first,secondReplay.Character),
                "第二步按真实材料原子交付，重放不重复扣料或奖励");
            var current = repository.FindMarriage(relation.Id)!;
            repository.CommitMarriageSettlement(current, current with { Revision = current.Revision + 1, State = 4 });
            first = repository.LoadCharacter(firstOwner, first.Slot); second = repository.LoadCharacter(secondOwner, second.Slot);
            instance = Guid.NewGuid();
            var own = Prepare(first, instance, null, recommended: true);
            var beforeOwn = first;
            first = Commit(firstOwner, beforeOwn, own).Character;
            var duplicate = Commit(firstOwner, beforeOwn, own);
            That(duplicate.Duplicate && first.Marriage?.Members[first.Id].CapsuleGauge == catalog.GrowthCapsule.Gage,
                "已婚角色按自身推荐资格累计胶囊，重放不加倍");
            own.Run.ConfirmCommittedDeath(own.Pending);
            var partner = Prepare(second, instance, null, recommended: true);
            second = Commit(secondOwner, second, partner).Character;
            That(second.Marriage is { } both && both.Members[first.Id].CapsuleGauge == catalog.GrowthCapsule.Gage &&
                both.Members[second.Id].CapsuleGauge == catalog.GrowthCapsule.Gage,
                "同共享实例的双方胶囊独立累计，不能被聚合去重吞掉后者");
            partner.Run.ConfirmCommittedDeath(partner.Pending);
            var nonRecommended = Prepare(first, Guid.NewGuid(), null);
            first = Commit(firstOwner, first, nonRecommended).Character;
            That(first.Marriage?.Members[first.Id].CapsuleGauge == catalog.GrowthCapsule.Gage,
                "普通非推荐清图不能伪增成长胶囊");

            Character Create(AccountOwners.Lease owner, string name)
            {
                var source = SkillRepositoryTests.Fixture(name, towns, characters, learning);
                var state = SkillPointLedger.Synchronize(0, 0, 30, source.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(30), BonusSp = 0, BonusTp = 0 }, learning);
                var material = items.Items.Get(10002610);
                var range = ItemSlotBoundService.Range(material.Kind,source.Inventory);
                short slot = (short)Enumerable.Range(range.Start,range.End-range.Start+1).First(index=>
                    source.Inventory.Items.All(item=>item.Position!=new InventorySlot(InventoryListType.Main,(short)index)));
                return repository.Create(owner, source with { Level = 30, InitialLearning = state, DungeonState = CharacterDungeonState.NewOrdinary,
                    Inventory = source.Inventory with { Items = source.Inventory.Items.Add(new(0,new(InventoryListType.Main,slot),
                        InventoryRewardGrantService.Create(material,1))) },
                    Skills = state.Trees.Single(tree => tree.Context == state.ActiveContext).Skills.Select(skill => new CharacterSkill(skill.Id, skill.Level)).ToArray() });
            }
            Character Accept(Character character, AccountOwners.Lease owner) => repository.CommitQuestLifecycle(owner,
                QuestLifecyclePlan.Accept(character, quests.Get(20737), quests, items.Items, items.GoldLimits, items.EpicPieces, DateTimeOffset.UtcNow)).Character;
            DungeonGrowthCommit Commit(AccountOwners.Lease owner, Character character, (SinglePlayerDungeonRun Run, PreparedDungeonDeath Pending) clear) =>
                repository.CommitDungeonGrowth(owner, character, clear.Run, clear.Pending,
                    new("fixture-only", DungeonKillExperienceCatalog.ResourcePath, clear.Pending.Monster.MonsterId, clear.Pending.Monster.CandidateLevel, 1), growth);
            void Sql(string text) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery(); }
            long Scalar(string text) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = text; return Convert.ToInt64(command.ExecuteScalar()); }
            SqliteConnection Open() { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); return db; }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
        Console.WriteLine($"PASS {Count - checks} marriage preparation ledger and capsule transaction assertions");
    }

    static InventoryPlan Noop(Character character) => new(character.Inventory, character.Inventory, "marriage_clear_fixture");

    static (SinglePlayerDungeonRun Run, PreparedDungeonDeath Pending) Prepare(Character character, Guid instance,
        QuestMarriagePartyClear? witness, bool recommended = false)
    {
        var map = new DungeonMap("boss", new(0, 0), 76126, "map/marriage-clear-fixture.map");
        var maze = new DungeonMaze(0, 1, 1, "fixture-only", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(character.Id, character.Revision, 3, 0, byte.MaxValue, 0, maze, map, map)
            { QuestAcceptances = character.ActiveMainQuests.ToImmutableArray(), RecommendedQuestClear = recommended };
        var monster = new DungeonRoomMonster(1, [1, 0, 3, 100, 100, 0, 1, 1], "[fixed]", "[boss]", "monster/marriage-clear-fixture.mob");
        var plan = DungeonMonsterPlan.FromRoom(new(map, [monster], [], []), character.Id, 20);
        var authority = new FixtureAuthority();
        if (!SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new([plan], []), authority, out var created, out var reason))
            throw new InvalidOperationException("Marriage clear fixture failed: " + reason);
        var run = created!;
        run.BindPartyInstance(instance);
        run.MarriagePartyClear = witness;
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        return (run, run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, plan.Monsters.Single().ObjectKey)).Pending!);
    }

    sealed class FixtureAuthority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key) => ConfirmDeath(identity, Guid.NewGuid(), key, 1);
    }
}
