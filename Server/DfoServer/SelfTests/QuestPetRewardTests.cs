using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Infrastructure;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestPetRewardTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources items,
        SkillLearningCatalog learning, CharacterGrowthCatalog growth, QuestRewardParameterCatalog parameters)
    {
        int count = Count;
        // 仅复用2675的真实奖励模板验证共享领奖能力，不移除实际任务的专用角色资格。
        var quest = QuestEligibilityTests.Quest(100) with
        {
            Condition = "[meet npc]", IntData = [2], Npc = 2, CompleteNpc = 2,
            Rewards = [new(0,100331543,null),new(0,1,null),new(0,63493,null),new(0,1,null)],
            Attributes = ["not give exp quest"], Definition = new([]) { Job = "[all]" }
        };
        That(items.Items.Get(63493).Kind == ItemKind.Creature && items.Items.Get(100331543).Kind == ItemKind.Equipment,
            "风云儿实际奖励分别属于Pet宠物与Main称号");
        var catalog = new QuestCatalog(new Dictionary<int,StoryQuest> { [100] = quest });
        That(QuestCoverage.Scan(catalog,items.Items,items.EpicPieces,parameters).Quests.Single().ImplementedPath,
            "已闭合的宠物领奖不再误判成无持有域");
        using (var f = new QuestOrdinaryCompletionTests.Fixture(towns,characters,learning,growth,parameters,items,quest,
            materialCount:0,active:[new(0,100,0)]))
        {
            var before = f.Read();
            f.Sql("CREATE TRIGGER pet_reward_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'pet reward rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue),"宠物领奖末段故障必须原子失败");
            That(Same(before,f.Read()) && f.Receipts == 0,"宠物详情、称号、完成位与回执全部回滚");
            f.Sql("DROP TRIGGER pet_reward_abort");
            var result=f.Finish(ushort.MaxValue);
            That(result.Outcome == QuestFinishOutcome.Committed,"回滚后重试原冻结宠物奖励计划："+result.Reason);
            var commit=result.Receipt!.Commit;
            var pet=commit.Rewards.Single(row=>row.Item.Core.ItemId==63493).Item;
            That(pet is { Uid: >0, Position.List: InventoryListType.Pet, Position.Index:0, Creature:not null } &&
                pet.Core.Value==pet.Uid && commit.Rewards.Single(row=>row.Item.Core.ItemId==100331543).Item.Position.List==InventoryListType.Main,
                "同事务返回两个真实持有域及已分配宠物UID");
            pet.ValidateDetails(allocating:false);
            var saved=f.Read();
            That(saved.Inventory.Find(pet.Position)==pet && saved.CompletedQuests.Contains(100),"重新读取保留完整宠物详情与完成位");
            _=f.Finish(ushort.MaxValue);
            That(Same(saved,f.Read()) && f.Receipts==1,"重复完成不增发宠物或称号");
        }
        using (var f=new QuestOrdinaryCompletionTests.Fixture(towns,characters,learning,growth,parameters,items,quest,
            materialCount:0,active:[new(0,100,0)],loseResult:true))
        {
            TestCheck.Reject<IOException>(()=>f.Finish(ushort.MaxValue),"宠物事务已提交而网络结果丢失");
            var saved=f.Read();
            var retry=f.Finish(ushort.MaxValue);
            That(retry.Receipt is { Commit.Duplicate:true } && Same(saved,f.Read()) && f.Receipts==1 &&
                retry.Receipt.Commit.Rewards.Single(row=>row.Item.Core.ItemId==63493).Item==saved.Inventory.Items.Single(item=>item.Core.ItemId==63493),
                "结果丢失重试恢复原宠物UID与详情，不重新创建实例");
        }
        foreach (bool fullPet in new[] {true,false})
        {
            using var f=new QuestOrdinaryCompletionTests.Fixture(towns,characters,learning,growth,parameters,items,quest,
                materialCount:0,active:[new(0,100,0)],fullEquipment:!fullPet,configure: source=>
                {
                    if (!fullPet) return source;
                    var petCore=InventoryRewardGrantService.Create(items.Items.Get(63493));
                    return source with { Inventory=source.Inventory with { Items=source.Inventory.Items.AddRange(
                        Enumerable.Range(0,140).Select(slot=>new InventoryItem(0,new(InventoryListType.Pet,(short)slot),petCore)
                        { Creature=new() })) } };
                });
            var before=f.Read();
            var result=f.Finish(ushort.MaxValue);
            That(result.Outcome==QuestFinishOutcome.InsufficientSpace && Same(before,f.Read()) && f.Receipts==0,
                fullPet ? "Pet满时称号也不部分发放" : "Main满时宠物也不部分发放");
        }
        Console.WriteLine($"PASS {Count-count} 任务宠物跨持有域奖励与原子回滚断言");
    }
}
