using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.Game.Titles;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using Microsoft.Data.Sqlite;
using PvfLib;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static partial class QuestTicketTests
{
    static async Task RunBatchAndCouponAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        SkillLearningCatalog skills,CharacterGrowthCatalog growth,QuestRewardParameterCatalog parameters)
    {
        int checks=Count;
        foreach (int id in new[] {10092768,10093749,10164630,2683657,10003197,10003198,10003199,10003200,10003201,10003230})
            That(items.Items.QuestTicket(id).ActionIndex==(id==10164630 ? 171u : id is 10092768 or 10093749 ? 101u : 110u),
                "当前PVF的批量及单任务券均保留真实动作: "+id);
        var raw=QuestEligibilityTests.Quest(100);
        StoryQuest[] definitions=[raw with {MinimumLevel=10},
            raw with {Id=101,MinimumLevel=95},
            raw with {Id=102,Grade="[normal]",MinimumLevel=20},
            raw with {Id=103,Grade="[mission]",MinimumLevel=20},
            raw with {Id=104,Grade="[mission]",MinimumLevel=21},
            raw with {Id=105,Grade="[achievement]",RewardType="[title]",MinimumLevel=20,Rewards=[new(0,26596,null),new(0,1,null)]},
            raw with {Id=106,Grade="[achievement]",MinimumLevel=20},
            raw with {Id=107,Grade="[achievement]",RewardType="[title]",MinimumLevel=21,Rewards=[new(0,26598,null),new(0,1,null)]}];
        var realQuests=SelfTestCatalogs.Quests(pvf); var titles=new TitleCatalog(pvf,realQuests);
        var titleService=new TitleAchievementService(titles,realQuests,items.Items,items.GoldLimits,items.EpicPieces);
        string root=Path.Combine(Path.GetTempPath(),"S5A9-batch-coupon-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var log=new ServerLog(root);
            foreach (var (id,action,level,expectedIds) in new (int,uint,byte,int[])[]
                {(10164630,171,90,[100,101]),(10092768,101,20,[100,102,103,105]),(10093749,101,20,[100,102,103,105])})
            using (var f=Fixture(id,level,definitions,[Active(0,100),Active(1,103),Active(2,106)]))
            {
                using var output=new ControlledOutput(); using var stop=new CancellationTokenSource();
                await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
                var commands=new GameCommandRegistry(items.Items);
                new StackableActionHandler(f.Catalog,items.Items,new QuestProjection(f.Catalog),log).Register(commands);
                new QuestQuickClearHandler(f.Catalog,QuestConnectionCatalog.Parse([],_=>""),new QuestProjection(f.Catalog),log,items.Items).Register(commands); commands.Seal();
                var context=Context(f,output,sender,stop); var before=f.Read();
                if (action==101)
                {
                    await commands.DispatchAsync(context,new Packet(1,1430,BitConverter.GetBytes(id)));
                    That(output.Opcodes.SequenceEqual(new ushort[] {1430}) && output.Frames[0][16]==1 && Same(before,f.Read()),
                        "101原生确认先按模板1430准备，再由515消费；准备不写完成或发称号");
                    output.Frames.Clear();
                }
                f.Sql("CREATE TRIGGER batch_coupon_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'batch rollback'); END");
                await RejectAsync<SqliteException>(()=>commands.DispatchAsync(context,Use(65,action)),"批量券晚SQL失败");
                That(output.Frames.Count==0 && Same(before,f.Read()),"批量完成、称号、扣券全部回滚");
                f.Sql("DROP TRIGGER batch_coupon_abort"); await commands.DispatchAsync(context,Use(65,action));
                var after=f.Read();
                That(after.CompletedQuests.SequenceEqual(expectedIds) && after.Level==before.Level &&
                    JsonSerializer.Serialize(after.Learning)==JsonSerializer.Serialize(before.Learning) && after.Inventory.Gold==before.Inventory.Gold &&
                    after.Inventory.Find(Bag(65)) is null && after.ActiveMainQuests.All(row=>!expectedIds.Contains(row.QuestId)),
                    "171不受人物等级限目标且仅主线；101包含本级主线normal/mission及严格称号，不发普通奖励");
                That(action==101 ? after.TitleProgress.Count==98 && after.Inventory.Items.Count(row=>row.Core.ItemId==26596)==1 :
                    after.TitleProgress.IsEmpty && !after.Inventory.Items.Any(row=>row.Core.ItemId==26596),"仅101发严格任务称号及general全组");
                Bytes(output.Frames[0].AsSpan(16),new byte[] {1,65,0,0,(byte)action,0,0,0},"批量券原生动作的8B ACK");
                output.Frames.Clear(); await commands.DispatchAsync(context,Use(65,action));
                That(!output.Opcodes.Contains((ushort)515) && Same(after,f.Read()),"批量券重放不重复扣源或发称号");
            }

            var rewardQuest=raw with {MinimumLevel=20,Condition="[seeking]",IntData=[10165055,30],
                Rewards=[new(0,2058,null),new(0,3,null),new(0,0,null),new(0,0,null)],
                Definition=raw.Definition! with {SelectRewards=[new(0,10403,null),new(0,1,null)]}};
            StoryQuest[] couponQuests=[rewardQuest,raw with {Id=101,MinimumLevel=20,Definition=raw.Definition! with {PrerequisiteGroups=[[100]]}},
                raw with {Id=102,MinimumLevel=21},raw with {Id=103,Grade="[normal]"},
                raw with {Id=104,Definition=raw.Definition! with {PrerequisiteGroups=[[102]]}}];
            foreach (bool accepted in new[] {false,true})
            using (var f=Fixture(10003230,20,couponQuests,accepted ? [Active(0,100,30)] : []))
            {
                using var output=new ControlledOutput(); using var stop=new CancellationTokenSource();
                await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
                var commands=new GameCommandRegistry(items.Items);
                var projection=new QuestProjection(f.Catalog);
                new QuestCompletionHandler(f.Catalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,log,
                    new CharacterLevelProjection(new CharacterStatComputer(characters),projection,skills),projection).Register(commands); commands.Seal();
                var context=Context(f,output,sender,stop); var before=f.Read();
                foreach (ushort rejected in new ushort[] {102,103,104})
                {
                    await commands.DispatchAsync(context,FinishCoupon(rejected));
                    That(output.Frames.Last()[16]==0 && Same(before,f.Read()),"单任务券拒绝超等级、非主线和未满足前置的未接任务");
                }
                output.Frames.Clear();
                f.Sql("CREATE TRIGGER one_coupon_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'coupon rollback'); END");
                await RejectAsync<SqliteException>(()=>commands.DispatchAsync(context,FinishCoupon(100)),"单任务券末段SQL失败");
                That(output.Frames.Count==0 && Same(before,f.Read()) && f.Receipts==0,"单任务券扣源、普通奖励、经验、完成位及接取同事务回滚");
                f.Sql("DROP TRIGGER one_coupon_abort"); await commands.DispatchAsync(context,FinishCoupon(100));
                var after=f.Read();
                That(after.CompletedQuests.SequenceEqual([100]) && after.Inventory.Find(Bag(65)) is {Core.ItemId:2058} replacement &&
                    replacement.Uid!=before.Inventory.Find(Bag(65))!.Uid &&
                    after.Inventory.Items.Where(row=>row.Core.ItemId==2058).Sum(row=>row.Core.Count)==3 &&
                    after.Inventory.Items.Any(row=>row.Core.ItemId==10403) && after.Inventory.Gold>before.Inventory.Gold &&
                    after.Learning.Experience>before.Learning.Experience && f.Receipts==1 && after.ActiveMainQuests.Length==0,
                    $"单主线券结果: completed={string.Join(',',after.CompletedQuests)},source={after.Inventory.Find(Bag(65))?.Core.ItemId},stk={after.Inventory.Items.Where(row=>row.Core.ItemId==2058).Sum(row=>row.Core.Count)},equ={after.Inventory.Items.Count(row=>row.Core.ItemId==10403)},gold={before.Inventory.Gold}->{after.Inventory.Gold},exp={before.Learning.Experience}->{after.Learning.Experience},receipts={f.Receipts},active={after.ActiveMainQuests.Length}");
                That(output.Opcodes.Contains((ushort)34) && !output.Opcodes.Contains((ushort)515) &&
                    output.Frames.First(frame=>BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1))==34)[16]==1,
                    "110原生确认只用34真实领奖及绝对14同步源，不伪造515消费");
                That(output.Opcodes.First()==14 && output.Opcodes.Skip(1).First()==34 && output.Opcodes.Skip(2).Contains((ushort)14),
                    "源耗尽槽被奖励复用时先14清旧券、再34奖励增量、最后14完整实际库存，不让奖励窗引用旧券");
                output.Frames.Clear(); await commands.DispatchAsync(context,FinishCoupon(100));
                That(Same(after,f.Read()) && f.Receipts==1,"单任务券重放已完成任务不重复发奖");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(Same(after,f.Session.Selected!),"重选恢复单任务券消费、真实奖励UID与主线完成");
            }

            using (var f=Fixture(10003230,20,couponQuests,[Active(0,100,30)],full:true))
            {
                var before=f.Read();
                var rejected=f.Session.FinishQuest(100,ushort.MaxValue,f.Catalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,couponSlot:65);
                That(rejected.Outcome==QuestFinishOutcome.InsufficientSpace && Same(before,f.Read()) && f.Receipts==0,
                    "单任务券奖励无空间时整批拒绝，不扣券、不清任务、不发部分经验或物品");
            }
            var clearQuest=raw with {MinimumLevel=20,Condition="[clear map]",IntData=[1],Rewards=[new(0,2058,null),new(0,1,null)]};
            using (var f=Fixture(10003230,20,[clearQuest],[Active(0,100,1)],lost:true))
            {
                var ordinary=f.Finish(ushort.MaxValue);
                That(ordinary.Outcome==QuestFinishOutcome.Rejected && f.Receipts==0,"无券普通34仍须本次接取的真实通关凭据");
                QuestFinishResult Finish() => f.Session.FinishQuest(100,ushort.MaxValue,f.Catalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,couponSlot:65);
                TestCheck.Reject<IOException>(()=>Finish(),"单任务券提交后返回值丢失");
                var persisted=f.Read(); var recovered=Finish();
                That(recovered.Receipt is {Commit.Duplicate:true,Commit.ProjectionCurrent:true,Commit.Clear:null,Commit.ConditionType:4} &&
                    recovered.Receipt.Commit.Character.CompletedQuests.SequenceEqual([100]) && Same(persisted,f.Read()) && f.Receipts==1 &&
                    Scalar(f,"SELECT count(*) FROM character_quest_clear_receipts")==0,
                    "单券清图授权不伪造通关事实，丢失返回值只恢复既有完成、奖励和扣源");
            }
            var question=raw with {Condition="[question]",Definition=raw.Definition! with {Fields=[new("question info",
                [new(6,0,"标题"),new(6,0,"正文"),new(6,0,"分支一"),new(6,0,"分支二")],[])]}};
            StoryQuest[] branches=[question,raw with {Id=101,Definition=raw.Definition! with {RequiredAnswers=[new(100,0)]}},
                raw with {Id=102,Definition=raw.Definition! with {RequiredAnswers=[new(100,1)]}}];
            foreach (uint answer in new[] {QuestQuestionProgress.Unanswered,1u})
            using (var f=Fixture(10003230,20,branches,[Active(0,100,answer)]))
            {
                var result=f.Session.FinishQuest(100,ushort.MaxValue,f.Catalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,couponSlot:65);
                That(result.Receipt is not null && result.Receipt.Commit.Character.QuestAnswers[100]==(answer==QuestQuestionProgress.Unanswered ? 0 : 1) &&
                    result.Receipt.Commit.Character.CompletedQuests.SequenceEqual([100]),
                    "单任务券保留已选问题分支；未回答选首个普通后继，不代选旅程跳过或连清后继");
            }

            QuestOrdinaryCompletionTests.Fixture Fixture(int template,byte level,StoryQuest[] scope,ActiveMainQuest[] active,bool full=false,bool lost=false) =>
                new(towns,characters,skills,growth,parameters,items,scope[0],materialCount:0,definitions:scope,active:active,
                    fullEquipment:full,loseResult:lost,questEffects:new(items.Items,skills,items.GoldLimits,items.EpicPieces,titleService),configure:candidate=>
                    {
                        var inventory=candidate.Inventory with {Items=candidate.Inventory.Items.Add(new(0,Bag(65),
                            InventoryRewardGrantService.Create(items.Items.Get(template)) with {ExpireTime=0}))};
                        if (full)
                            for (short slot=ItemSlotBoundService.QuickSlotStart;slot<=ItemSlotBoundService.QuickSlotEnd;slot++)
                                inventory=inventory with {Items=inventory.Items.Add(new(0,Bag(slot),InventoryRewardGrantService.Create(items.Items.Get(10403))))};
                        return candidate with {Level=level,InitialLearning=SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,level,
                            candidate.InitialLearning with {Experience=growth.GetLevelStartExperience(level)},skills),Inventory=inventory};
                    });
        }
        finally { TestFiles.DeleteDirectory(root,true); }
        Console.WriteLine($"PASS {Count-checks} 101/171批量范围及110单主线原生领奖、奖励、原子恢复断言");
    }

    static Packet FinishCoupon(ushort quest,ushort source=65)
    {
        byte[] body=new byte[10]; BinaryPrimitives.WriteUInt16LittleEndian(body,34);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2),quest);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4),ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6),1);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8),source);
        return new(1,34,body);
    }
}
