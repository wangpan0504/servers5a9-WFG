using DfoServer.Game.Characters;
using DfoServer.Infrastructure;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using static DfoServer.QuestCompletionRepositoryTests;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestAntonUnlockTests
{
    public static void RunCore()
    {
        var quest = QuestEligibilityTests.Quest(12829) with
        {
            Path = "n_quest/anton/anton_dimension_enter.qst", Grade = "[side]", Difficulty = "F",
            Npc = 332, CompleteNpc = -1, MinimumLevel = 85, MaximumLevel = 99,
            Condition = "[seeking]", SubType = -1, IntData = [3262, 100],
            DeclaredGoldMultiple = 10, DeclaredIgnoreLevelForExperience = 1,
            Rewards = new[] { 10095688, 3, 10095689, 3, 10095690, 3, 10095687, 1 }
                .Select(value => new QuestRewardToken(0, value, null)).ToArray(),
            Definition = new([]) { Job = "[all]", ExposedByNpc = false, PrerequisiteGroups = [[3779]] }
        };
        var catalog = new QuestCatalog(new[] { quest, QuestEligibilityTests.Quest(3779) }.ToDictionary(row => row.Id));
        var character = new Character(1, 0, "Anton", 0, new(0, 0, 0, 0), null)
            { Level = 85, CompletedQuests = [3779] };
        That(QuestAntonUnlockEntry.Supports(quest) && !quest.Definition.ExposedByNpc &&
            QuestEligibility.Candidates(character, catalog).SequenceEqual([12829]),
            "奈恩窄入口仅增加合格候选，不改原exposed0");
        foreach (var rejected in new[] { character with { Level = 84 }, character with { CompletedQuests = [] },
            character with { CompletedQuests = [3779, 12829] }, character with { ActiveMainQuests = [new(0, 12829, 1)] } })
            That(!QuestEligibility.Evaluate(rejected, quest, catalog).Eligible, "安徒恩原等级、前置、一次接取与完成规则不绕过");
        foreach (var changed in new[]
        {
            quest with { Id = 12830 }, quest with { Path = "n_quest/other.qst" }, quest with { Grade = "[hidden]" },
            quest with { MinimumLevel = 84 }, quest with { Npc = 2 }, quest with { CompleteNpc = 332 },
            quest with { IntData = [3262, 1] }, quest with { Rewards = [new(0, 10095687, null), new(0, 99, null)] },
            quest with { Definition = quest.Definition with { AccountQuest = true } },
            quest with { Definition = quest.Definition with { IsEvent = true } },
            quest with { Definition = quest.Definition with { PrerequisiteGroups = [[3779], [1]] } },
            quest with { Definition = quest.Definition with { FirstExposedByNpc = 1 } }
        })
            That(!QuestAntonUnlockEntry.Supports(changed) && QuestEligibility.InspectResource(changed).Entry != QuestResourceEntry.Ordinary,
                "相邻任务或改变资源合同不能继承奈恩豁免");
        var unknown = quest with { Definition = quest.Definition with { Fields = [new("unknown unlock field", [], [])] } };
        That(!QuestCoverage.HasOrdinaryDeclarations(unknown), "入口不吞掉未知资源声明");
    }
    internal static Location Nain(TownCatalog towns)
    {
        var completed = new HashSet<int> { 3779 };
        return towns.Areas.Values.Where(area => area.Map(completed).Npcs.Any(npc => npc.Id == 332))
            .SelectMany(area => area.Map(completed).Bounds.Select(region =>
                (area.Town, area.Area, X: region.Bounds.X + region.Bounds.Width / 2, Y: region.Bounds.Y + region.Bounds.Height / 2)))
            .Where(point => point.X is >= 0 and <= ushort.MaxValue && point.Y is >= 0 and <= ushort.MaxValue)
            .Select(point => new Location(point.Town, point.Area, (ushort)point.X, (ushort)point.Y))
            .First(position => towns.IsPosition(position, completed));
    }

    public static void RunResources(TownCatalog towns, CharacterCatalog characters, ItemResources items,
        SkillLearningCatalog learning, CharacterGrowthCatalog growth, QuestRewardParameterCatalog parameters, QuestCatalog quests)
    {
        var quest = quests.Get(12829);
        That(QuestAntonUnlockEntry.Supports(quest) && QuestCoverage.HasOrdinaryDeclarations(quest),
            "真实12829完整合同匹配窄入口，未知声明仍经普通校验");
        var position = Nain(towns);
        NewCharacter Configure(NewCharacter source) => source with
        {
            Position = position, Level = 85, AcceptedStoryLevel = 85, CompletedQuests = [3779],
            InitialLearning = SkillPointLedger.Synchronize(source.Job, source.GrowType, 85,
                source.InitialLearning with { Experience = growth.GetLevelStartExperience(85) }, learning)
        };
        using var f = new QuestOrdinaryCompletionTests.Fixture(towns, characters, learning, growth, parameters,
            items, quest, materialCount: 0, active: [], definitions: quests.All.ToArray(), configure: Configure, resumeTown: true);
        That(QuestAntonUnlockEntry.AtNpc(f.Read(), towns) && !CharacterRosterEntry.FromCharacter(f.Read()).AntonEligible,
            "未交付者即使在奈恩地图也无安徒恩频道资格");
        That(f.Change(12829, QuestLifecycleAction.Accept)?.Outcome == QuestLifecycleOutcome.Committed,
            "真实奈恩地图接取原寻物任务");
        var before = f.Read();
        That(f.Finish(ushort.MaxValue, 12829).Outcome == QuestFinishOutcome.Rejected && Same(before, f.Read()),
            "没有账号金色小晶块时拒绝交付且不写完成");
        That(f.Session.PlanAndCommitInventory(character => new InventoryResult(true, 0,
            new InventoryPlan(character.Inventory, character.Inventory with
            { AccountWallet = character.Inventory.AccountWallet.Set(CurrencyKind.GoldCube, 100) }, "anton_test_funding"))).Accepted,
            "夹具通过真实账号钱包事务准备100材料");
        before = f.Read();
        f.Sql("CREATE TRIGGER anton_receipt_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'anton rollback'); END");
        Testing.TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue, 12829), "安徒恩回执末段失败");
        That(Same(before, f.Read()) && f.Receipts == 0, "失败回滚钱包revision、100材料、奖励和完成位");
        f.Sql("DROP TRIGGER anton_receipt_abort");
        var result = f.Finish(ushort.MaxValue, 12829);
        That(result.Outcome == QuestFinishOutcome.Committed, "原安徒恩寻物交付：" + result.Reason);
        var saved = f.Read();
        That(saved.Inventory.AccountWallet.Get(CurrencyKind.GoldCube) == 0 &&
            saved.Inventory.AccountWallet.Revision == before.Inventory.AccountWallet.Revision + 1 &&
            saved.CompletedQuests.Contains(12829) && CharacterRosterEntry.FromCharacter(saved).AntonEligible &&
            result.Receipt!.Commit.Rewards.Length == 4 && result.Receipt.Commit.Rewards.All(row => row.Item.Uid > 0) &&
            result.Receipt.Commit.Rewards.Select(row => row.GrantedCount).SequenceEqual([3,3,3,1]),
            "只扣一次真实账号钱包，四项原奖励拥有真实UID，完成事实才授予频道资格");
        That(f.Finish(ushort.MaxValue, 12829).Outcome == QuestFinishOutcome.AlreadyCompleted &&
            Same(saved, f.Read()) && f.Receipts == 1, "重复交付不重复扣费发奖");
        f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
        That(Same(saved, f.Read()) && !QuestEligibility.Candidates(f.Read(), quests).Contains(12829),
            "重选保留钱包、奖励及完成位，候选不再出现");
        var second = f.Repository.Create(f.Owner, Configure(CharacterCreation.Prepare("AntonAlt", characters.Get(0),
            position, towns.InitialReturn, learning.Get)));
        That(second.Inventory.AccountWallet.Get(CurrencyKind.GoldCube) == 0 && !second.CompletedQuests.Contains(12829) &&
            !CharacterRosterEntry.FromCharacter(second).AntonEligible, "同账号另一角色共享余额但不共享安徒恩解锁完成");
        f.Session.ReturnToRoster(); f.Session.Select(second.Slot, resumeTown: true); f.Session.MarkReady();
        That(f.Change(12829, QuestLifecycleAction.Accept)?.Outcome == QuestLifecycleOutcome.Committed &&
            f.Finish(ushort.MaxValue, 12829).Outcome == QuestFinishOutcome.Rejected,
            "第二角色可独立接取，但共享余额不足不能免费完成");
        using (var lost = new QuestOrdinaryCompletionTests.Fixture(towns, characters, learning, growth, parameters,
            items, quest, materialCount: 0, active: [], definitions: quests.All.ToArray(), configure: Configure,
            resumeTown: true, loseResult: true))
        {
            That(lost.Change(12829, QuestLifecycleAction.Accept)?.Outcome == QuestLifecycleOutcome.Committed,
                "返回丢失夹具沿真实奈恩接取");
            That(lost.Session.PlanAndCommitInventory(character => new InventoryResult(true, 0,
                new InventoryPlan(character.Inventory, character.Inventory with
                { AccountWallet = character.Inventory.AccountWallet.Set(CurrencyKind.GoldCube, 100) }, "anton_test_funding"))).Accepted,
                "返回丢失夹具真实钱包准备");
            var selected = lost.Session.Selected;
            Testing.TestCheck.Reject<IOException>(() => lost.Finish(ushort.MaxValue, 12829), "安徒恩事务已提交但返回丢失");
            var persisted = lost.Read();
            That(ReferenceEquals(selected, lost.Session.Selected) && persisted.CompletedQuests.Contains(12829) &&
                persisted.Inventory.AccountWallet.Get(CurrencyKind.GoldCube) == 0, "未知结果不提前安装，真实扣费已持久");
            var recovered = lost.Finish(ushort.MaxValue, 12829);
            That(recovered.Receipt?.Commit is { Duplicate: true, ProjectionCurrent: true } && lost.Lost!.SamePlan &&
                Same(persisted, lost.Read()) && lost.Receipts == 1 && lost.Session.CanPublishQuestCompletion(recovered.Receipt),
                "相同34恢复冻结计划与同一回执，不再扣钱包或分配奖励UID");
        }
        using var wrong = new QuestOrdinaryCompletionTests.Fixture(towns, characters, learning, growth, parameters,
            items, quest, materialCount: 0, active: [], definitions: quests.All.ToArray(),
            configure: source => Configure(source) with { Position = towns.Room });
        var wrongBefore = wrong.Read();
        That(wrong.Change(12829, QuestLifecycleAction.Accept)?.Outcome == QuestLifecycleOutcome.Rejected &&
            Same(wrongBefore, wrong.Read()), "远离奈恩地图的伪造31不改变存档");
    }

}
