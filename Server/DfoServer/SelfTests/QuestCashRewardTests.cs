using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestCashRewardTests
{
    internal static StoryQuest Quest(int id, params int[] rewards) => QuestEligibilityTests.Quest(id) with
    {
        Condition = "[seeking]", CompleteNpc = -1, IntData = [10165055,1], Attributes = ["not give exp quest"],
        Rewards = rewards.Select(value => new QuestRewardToken(0,value,null)).ToArray()
    };

    public static void RunCore()
    {
        foreach (var kind in Enum.GetValues<CurrencyKind>())
            That(QuestCurrencyReward.HasBalanceNotice(kind) == (kind is CurrencyKind.Cera or CurrencyKind.TokenCera or CurrencyKind.HappyTokenCera),
                "53仅覆盖三个已确认现金余额，不把积分或租赁币泛化为现金");
        That(CurrencyWallet.RewardKind(2681762) == CurrencyKind.TokenCera &&
            CurrencyWallet.RewardKind(2681917) == CurrencyKind.HappyTokenCera && CurrencyWallet.RewardKind(2681918) is null,
            "沿原两券映射，不猜纯Cera或相邻物品映射");
    }

    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources items,
        SkillLearningCatalog learning, CharacterGrowthCatalog growth, QuestRewardParameterCatalog parameters, QuestCatalog quests)
    {
        var quest = Quest(100,2681762,7,2681917,9,2058,2);
        using (var f = Make())
        {
            Fund(f,11,22,33);
            var before = f.Read();
            f.Sql("CREATE TRIGGER cash_receipt_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'cash rollback'); END");
            Testing.TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue), "现金奖励末段SQL失败");
            That(Same(before,f.Read()) && f.Receipts == 0, "失败回滚材料、两券、实体奖励、账号revision及完成位");
            f.Sql("DROP TRIGGER cash_receipt_abort");
            var result = f.Finish(ushort.MaxValue);
            That(result.Outcome == QuestFinishOutcome.Committed, "混合现金奖励交付：" + result.Reason);
            var saved = f.Read(); var wallet = saved.Inventory.AccountWallet;
            That(wallet.Get(CurrencyKind.Cera) == 11 && wallet.Get(CurrencyKind.TokenCera) == 29 &&
                wallet.Get(CurrencyKind.HappyTokenCera) == 42 && wallet.Revision == before.Inventory.AccountWallet.Revision + 1 &&
                saved.Inventory.Items.Single(row => row.Core.ItemId == 10165055).Core.Count == 4 &&
                result.Receipt!.Commit.Rewards is [{ Item.Uid: > 0, GrantedCount: 2 }] &&
                result.Receipt.Commit.Currencies.Select(row => row.Count).SequenceEqual([7,9]) &&
                saved.Inventory.Items.All(row => row.Core.ItemId is not (2681762 or 2681917)),
                "两券各自全额入账号钱包，保留Cera；仅材料和实体奖励占真实槽/UID");
            That(f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.AlreadyCompleted && Same(saved,f.Read()) && f.Receipts == 1,
                "重复交付不重复现金、材料或实体奖励");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(saved,f.Read()), "重选恢复同一现金余额和完成事实");
            var sibling = Sibling(f);
            That(sibling.Inventory.AccountWallet.Revision == wallet.Revision && sibling.Inventory.AccountWallet.Balances.SequenceEqual(wallet.Balances) && !sibling.CompletedQuests.Contains(100),
                "另一角色共享现金但不共享普通任务完成");
        }
        foreach (var kind in new[] { CurrencyKind.TokenCera, CurrencyKind.HappyTokenCera })
        using (var f = Make())
        {
            Fund(f,11,kind == CurrencyKind.TokenCera ? int.MaxValue : 22,
                kind == CurrencyKind.HappyTokenCera ? int.MaxValue : 33);
            var before = f.Read();
            That(f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.Rejected && Same(before,f.Read()) && f.Receipts == 0,
                "任一券溢出整批拒绝，不饱和截断、不消耗材料/先发另一券");
        }
        using (var f = Make())
        {
            Fund(f,11,22,33);
            var before = f.Read(); var plan = f.Prepare(ushort.MaxValue).Plan!;
            var sibling = Sibling(f);
            f.Repository.CommitInventory(f.Owner,sibling,new(sibling.Inventory,sibling.Inventory with
                { AccountWallet = sibling.Inventory.AccountWallet.Set(CurrencyKind.HappyTokenCera,34) },"cash_sibling_change"));
            var changed = f.Read();
            That(changed.Revision == before.Revision && changed.Inventory.AccountWallet.Revision != before.Inventory.AccountWallet.Revision,
                "同账号另一角色仅改变共享钱包，不改变原角色revision");
            Testing.TestCheck.Reject<InvalidOperationException>(() => f.Repository.CommitQuestCompletion(f.Owner,plan),
                "旧角色计划不能覆盖另一角色更新的钱包");
            That(Same(changed,f.Read()) && f.Receipts == 0, "钱包竞争拒绝保持材料、现金与完成位");
        }
        foreach (bool laterWalletChange in new[] { false,true })
        using (var f = Make(lost:true))
        {
            Fund(f,11,22,33); var selected = f.Session.Selected;
            Testing.TestCheck.Reject<IOException>(() => f.Finish(ushort.MaxValue), "现金事务提交后返回丢失");
            if (laterWalletChange)
            {
                var sibling = Sibling(f);
                f.Repository.CommitInventory(f.Owner,sibling,new(sibling.Inventory,sibling.Inventory with
                    { AccountWallet = sibling.Inventory.AccountWallet.Set(CurrencyKind.HappyTokenCera,43) },"cash_after_receipt"));
            }
            var saved = f.Read(); var recovered = f.Finish(ushort.MaxValue);
            That(recovered.Receipt?.Commit is { Duplicate:true } commit && commit.ProjectionCurrent == !laterWalletChange &&
                f.Lost!.SamePlan && Same(saved,f.Read()) && f.Receipts == 1 &&
                f.Session.CanPublishQuestCompletion(recovered.Receipt) == !laterWalletChange &&
                (!laterWalletChange || ReferenceEquals(selected,f.Session.Selected)),
                "原34恢复同一现金回执；后续钱包变化时不安装或发布旧53");
        }
        foreach (int id in new[] { 2681762,2681917 })
        {
            var reward = Quest(101,id,1);
            var material = reward with { IntData = [id,1], Rewards = [new(0,2058,null),new(0,1,null)] };
            var gift = reward with { Definition = reward.Definition! with
                { AcceptanceRewards = new[] { new QuestRewardToken(0,id,null),new QuestRewardToken(0,1,null) }.ToImmutableArray() } };
            var coverage = QuestCoverage.Scan(new(new[] { reward,material with { Id=102 },gift with { Id=103 } }.ToDictionary(row=>row.Id)),
                items.Items,items.EpicPieces,parameters);
            That(coverage.Quests.Single(row=>row.Id==101).ImplementedPath &&
                coverage.Quests.Where(row=>row.Id is 102 or 103).All(row=>!row.ImplementedPath),
                "覆盖审计只认可现金奖励，不放开现金材料或接取赠品");
            using var consumed = Make(definition: material with { Id=100 });
            Fund(consumed,0,10,10);
            That(consumed.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.Rejected && consumed.Receipts == 0,
                "现金寻物消耗仍拒绝，不虚构Main槽");
        }
        foreach (int id in new[] { 20558,20559,20560 })
            That(QuestEligibility.InspectResource(quests.Get(id)).Entry == QuestResourceEntry.Dedicated &&
                quests.Get(id).Difficulty is null, "旧补偿入口与缺difficulty保持fail-closed：" + id);

        QuestOrdinaryCompletionTests.Fixture Make(bool lost=false, StoryQuest? definition=null) =>
            new(towns,characters,learning,growth,parameters,items,definition??quest,loseResult:lost);
        Character Sibling(QuestOrdinaryCompletionTests.Fixture f) => f.Repository.Create(f.Owner,
            CharacterCreation.Prepare("CashSibling",characters.Get(0),towns.Room,towns.InitialReturn,learning.Get));
    }

    static void Fund(QuestOrdinaryCompletionTests.Fixture f,int cera,int token,int happy) =>
        That(f.Session.PlanAndCommitInventory(character => new InventoryResult(true,0,new InventoryPlan(character.Inventory,
            character.Inventory with { AccountWallet = character.Inventory.AccountWallet.Set(CurrencyKind.Cera,cera)
                .Set(CurrencyKind.TokenCera,token).Set(CurrencyKind.HappyTokenCera,happy) },"cash_quest_fixture"))).Accepted,
            "隔离夹具经原钱包事务准备余额");
}
