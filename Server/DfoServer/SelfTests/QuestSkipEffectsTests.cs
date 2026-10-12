using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestSkipEffectsTests
{
    public static async Task RunAsync(TownCatalog towns,CharacterCatalog characters,ItemResources items,SkillLearningCatalog skills,
        CharacterGrowthCatalog growth,QuestRewardParameterCatalog parameters,ServerLog log,QuestCatalog actual)
    {
        var definitions=Definitions();
        using(var f=Fixture())
        {
            var before=f.Read(); var prepared=f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared!;
            That(prepared.ProfessionGrowType is null && prepared.EquipmentSlotFlagsAfter==0 &&
                prepared.Inventory.After.Items.Single(item=>item.Core.ItemId==10164759).Core.Count==1 &&
                prepared.ScopeQuestIds.Contains(29005) && new[]{29002,29003,29004,29006,29007,29010}.All(id=>!prepared.ScopeQuestIds.Contains(id)),
                "手册发真实任务道具，不推进觉醒、三个槽位或无关支线");
            That(prepared.TargetLearning.Experience==before.Learning.Experience && prepared.TargetLearning.BonusSp==137 &&
                prepared.TargetLearning.BonusTp==11 && before.Inventory.Items.All(item=>prepared.Inventory.After.Items.Contains(item)) &&
                !prepared.Inventory.After.Items.Any(item=>item.Core.ItemId==10403),"技能额外来源、旧实例和普通装备奖励边界保持");
            f.Sql("CREATE TRIGGER effect_abort AFTER INSERT ON character_quest_effect_receipts BEGIN SELECT RAISE(ABORT,'effect rollback'); END");
            Reject<SqliteException>(()=>f.Session.FinishManualQuestSkip(),"特殊物品资格末段失败");
            That(Same(before,f.Read()) && Scalar(f,"SELECT count(*) FROM character_quest_effect_receipts")==0,
                "任务物品、完成及两种回执整笔回滚，职业、技能和槽位保持原状态");
            f.Sql("DROP TRIGGER effect_abort");
            using var output=new Output(); using var stop=new CancellationTokenSource();
            await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
            var projection=new QuestProjection(f.Catalog); var commands=new GameCommandRegistry();
            new QuestQuickClearHandler(f.Catalog,QuestConnectionCatalog.Parse([],_=>""),projection,log,items.Items,skills).Register(commands);
            commands.Seal();
            var context=new GameConnectionContext(new(97,IPAddress.Loopback,output,stop),f.Owner,f.Session,sender);
            await commands.DispatchAsync(context,new(1,1457,[]));
            var after=f.Read();
            That(after.GrowType==1 && after.EquipmentSlotFlags==0 && after.Revision==before.Revision+1 &&
                after.Inventory.Items.Single(item=>item.Core.ItemId==10164759).Uid>0 &&
                output.Opcodes.SequenceEqual(new ushort[]{14,574,21,356}),"提交真实UID并发布任务物品和任务投影，主线不发布觉醒或槽位变化");
            That(f.Repository.CommitQuestQuickClear(f.Owner,prepared) is {Duplicate:true,ProjectionCurrent:true} &&
                Same(after,f.Read()),"带新UID的跳过回执精确恢复，不再次授予物品或觉醒");
            Consume(f,after);
            f.Session.ReturnToRoster(); f.Session.Select(0);
            long revision=f.Read().Revision;
            That(!f.Read().Inventory.Items.Any(item=>item.Core.ItemId==10164759) &&
                f.Read().Revision==revision,"新跳过物品用掉后再选角不补发、不把已跳过的一觉主线当作实际觉醒");
            f.Session.ReturnToRoster(); f.Repository.Delete(f.Owner,f.Repository.LoadDeletionTarget(f.Owner,0));
            That(Scalar(f,"SELECT count(*) FROM character_quest_effect_receipts")==0 &&
                Scalar(f,"SELECT count(*) FROM character_quest_special_reward_claims")==0,"删除角色清理任务效果回执及旧补领子表，不破坏原角色删除功能");
        }
        using(var f=Fixture(full:true))
        {
            var before=f.Read();
            That(f.Session.PrepareManualQuestSkip(f.Catalog) is {Prepared:null} && Same(before,f.Read()),
                "任务物品页满时拒绝整笔新跳过，不留下已完成任务、觉醒或槽位");
        }
        using(var f=Fixture())
        {
            var source=f.Read() with {GrowType=0x21,CompletedQuests=[3356,29005,29006]};
            var effect=new QuestSkipEffects(items.Items,skills,items.GoldLimits,items.EpicPieces);
            That(ReferenceEquals(effect.Prepare(source,[f.Catalog.Get(29005),f.Catalog.Get(29006)],
                    new(source.Inventory,source.Inventory,"gm"),0,source.GrowType,source.Learning).Learning,source.Learning),
                "已有GM二觉状态不重复同步技能，不因完成记录产生空补齐保存");
        }
        using(var f=Fixture())
        {
            var main=f.Read() with {CompletedQuests=actual.All.Where(quest=>quest.Grade=="[epic]" && quest.Definition?.JobChange==0).Select(quest=>quest.Id).Order().ToArray()};
            bool prepared=QuestQuickClearPlan.TryCreateManual(main,Guid.NewGuid(),actual,ImmutableDictionary<int,byte>.Empty,out var plan,out string reason,towns);
            That(!prepared || new[]{649,650,2636,4929}.All(id=>!plan!.ScopeQuestIds.Contains(id)) && plan!.ProfessionGrowType is null,
                "主线均完成后无可跳范围或仅完成剩余主线，独立槽位/二觉仍保持: "+reason);
        }

        var producer=definitions[0] with {Rewards=[new(0,10164759,null),new(0,2,null)],
            Definition=definitions[0].Definition! with {PrerequisiteGroups=[[28999],[3356]]}};
        var consumer=QuestEligibilityTests.Quest(28999) with {Condition="[seeking]",IntData=[10164759,1],
            Definition=new([]) {PrerequisiteGroups=[[29001]]}};
        var chain=new[]{producer,consumer,definitions[^1]};
        using(var f=Fixture(custom:chain))
        {
            var before=f.Read(); var plan=f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared!;
            That(plan.ScopeQuestIds.Contains(28999) && plan.Inventory.After.Items.Single(item=>item.Core.ItemId==10164759).Core.Count==1 &&
                plan.Inventory.After.Gold==before.Inventory.Gold && plan.Inventory.After.Items.Single(item=>item.Core.ItemId==10165055).Core.Count==5 &&
                plan.Inventory.After.Items.Single(item=>item.Core.ItemId==3037).Core.Count==9,
                "先获得2再交付1只保留1，编号反序与择一前置按真实可达路径处理，普通材料和金币不扣");
            f.Session.FinishManualQuestSkip();
            f.Session.ReturnToRoster();f.Session.Select(0);
            That(f.Read().Inventory.Items.Single(item=>item.Core.ItemId==10164759).Core.Count==1,
                "本次特殊物品净收支与任务同事务保存，再选角不重复扣除");
        }
        using(var f=Fixture(held:true,mapCount:2,custom:chain))
        {
            QuestQuickClearPlan.TryCreateManual(f.Read(),Guid.NewGuid(),f.Catalog,ImmutableDictionary<int,byte>.Empty,out var old,out _);
            var saved=f.Repository.CommitQuestQuickClear(f.Owner,old!).Character;
            f.Session.ReturnToRoster();f.Session.Select(0);
            That(Same(saved,f.Read()) && Same(saved,f.Session.Selected!) && f.Read().Inventory.Items.Single(item=>item.Core.ItemId==10164759).Core.Count==2,
                "旧跳过缺失效果保持原样；选角不扫描历史、不补领也不追加扣除");
        }
        using(var f=Fixture(full:true,custom:[producer,consumer with {IntData=[10164759,2]},definitions[^1]]))
        {
            var plan=f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared;
            That(plan is not null && !plan.Inventory.After.Items.Any(item=>item.Core.ItemId==10164759),
                "同批道具全部消耗无需临时背包空格，不把已交付物品留在包内");
            That(f.Session.FinishManualQuestSkip()!.Commit is not null,"任务物品完全抵消时仍原子完成，不写补领记录");
        }
        using(var f=Fixture(custom:[producer,consumer with {Grade="[side]",IntData=[10164759,1],Definition=new([])},definitions[^1]]))
        {
            f.Session.PrepareManualQuestSkip(f.Catalog);f.Session.FinishManualQuestSkip();
            f.Session.ReturnToRoster();f.Session.Select(0);f.Session.MarkReady();
            f.Change(28999,QuestLifecycleAction.Accept);
            That(f.Finish(ushort.MaxValue,28999).Receipt?.Commit is not null,"新跳过获得的特殊物品可被后续支线正常消耗");
            var saved=f.Read();f.Session.ReturnToRoster();f.Session.Select(0);
            That(Same(saved,f.Read()) && f.Read().Inventory.Items.Where(item=>item.Core.ItemId==10164759).Sum(item=>item.Core.Count)==1,
                "后续正常消耗后选角保持真实剩余量，已用物品不补发");
        }
        using(var f=Fixture(held:true,custom:[consumer with {Definition=new([]) {PrerequisiteGroups=[[3356]]}},definitions[^1]]))
        {
            var before=f.Read();f.Session.PrepareManualQuestSkip(f.Catalog);
            f.Sql("CREATE TRIGGER consume_abort AFTER INSERT ON character_quest_effect_receipts BEGIN SELECT RAISE(ABORT,'consume rollback'); END");
            Reject<SqliteException>(()=>f.Session.FinishManualQuestSkip(),"特殊物品消费末段SQL失败");
            That(Same(before,f.Read()),"扣除道具、完成记录及消费资格同事务回滚");f.Sql("DROP TRIGGER consume_abort");
            using var output=new Output();using var stop=new CancellationTokenSource();
            await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
            var commands=new GameCommandRegistry();new QuestQuickClearHandler(f.Catalog,QuestConnectionCatalog.Parse([],_=>""),new(f.Catalog),log,items.Items,skills).Register(commands);
            commands.Seal();var context=new GameConnectionContext(new(98,IPAddress.Loopback,output,stop),f.Owner,f.Session,sender);
            await commands.DispatchAsync(context,new(1,1457,[]));
            That(!f.Read().Inventory.Items.Any(item=>item.Core.ItemId==10164759) && output.Opcodes.SequenceEqual(new ushort[]{14,574,21,356}),
                "特殊任务道具完全扣空仍发布真实背包删除14，不只通知剩余非空格");
        }
        using(var f=Fixture(custom:[producer,consumer with {Grade="[side]",IntData=[10164759,2],Definition=new([])},definitions[^1]]))
        {
            f.Session.PrepareManualQuestSkip(f.Catalog);f.Session.FinishManualQuestSkip();
            f.Session.ReturnToRoster();f.Session.Select(0);f.Session.MarkReady();
            f.Change(28999,QuestLifecycleAction.Accept);
            That(f.Finish(ushort.MaxValue,28999).Receipt?.Commit is not null,"新跳过获得的特殊物品可被后续支线正常消耗");
            var saved=f.Read();f.Session.ReturnToRoster();f.Session.Select(0);
            That(Same(saved,f.Read()) && f.Read().Inventory.Items.Where(item=>item.Core.ItemId==10164759).Sum(item=>item.Core.Count)==0,
                "后续正常消耗后选角保持真实剩余量，已用物品不补发");
        }
        using(var f=Fixture(custom:[producer with {Rewards=[],Definition=producer.Definition! with {AcceptanceRewards=[new(0,10164759,null),new(0,1,null)]}},consumer,definitions[^1]]))
        {
            That(f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared is { } plan && !plan.Inventory.After.Items.Any(item=>item.Core.ItemId==10164759),
                "接取阶段获得的特殊任务道具也按后续交付抵消，不遗留赠品");
        }

        QuestOrdinaryCompletionTests.Fixture Fixture(bool held=false,bool full=false,ActiveMainQuest[]? active=null,
            int mapCount=1,StoryQuest[]? custom=null)
        {
            var local=custom ?? definitions;
            return new(towns,characters,skills,growth,parameters,items,local[0],materialCount:held||full ? 0 : 5,definitions:local,active:active??[],
                questEffects:new(items.Items,skills,items.GoldLimits,items.EpicPieces),
                configure: candidate=>
                {
                    var inventory=candidate.Inventory;
                    inventory=inventory with {Items=inventory.Items.Add(new(0,new(InventoryListType.Main,121),InventoryRewardGrantService.Create(items.Items.Get(3037),9)))};
                    if(held) inventory=inventory with { Items=inventory.Items.Add(new(0,new(InventoryListType.Main,177),InventoryRewardGrantService.Create(items.Items.Get(10164759),mapCount))) };
                    if(full)
                    {
                        var range=ItemSlotBoundService.Range(ItemKind.Quest,inventory.Expansion);
                        inventory=inventory with { Items=inventory.Items.AddRange(Enumerable.Range(range.Start,range.End-range.Start+1)
                            .Select(index=>new InventoryItem(0,new(InventoryListType.Main,(short)index),InventoryRewardGrantService.Create(items.Items.Get(10165054))))) };
                    }
                    return candidate with { Level=95,GrowType=1,CompletedQuests=[3356],Inventory=inventory,
                        InitialLearning=SkillPointLedger.Synchronize(candidate.Job,1,95,candidate.InitialLearning with
                        { Experience=growth.GetLevelStartExperience(95),BonusSp=137,BonusTp=11 },skills) };
                });
        }
    }

    static StoryQuest[] Definitions() => [
        QuestEligibilityTests.Quest(29001) with { Condition="[seeking]",IntData=[3037,1,0,100],Rewards=[new(0,10164759,null),new(0,1,null),new(0,10403,null),new(0,1,null)] },
        Slot(29002,60,0),Slot(29003,65,1),Slot(29004,90,2),
        Awake(29005,50,1,3356),Awake(29006,75,2,29005),
        Awake(29007,50,1,3356) with { Definition=new([]) {Job="[swordman]",GrowType=2,JobChange=2,PrerequisiteGroups=[[3356]]} },
        QuestEligibilityTests.Quest(29010) with {Grade="[side]"},QuestEligibilityTests.Quest(3356)];
    static StoryQuest Slot(int id,int level,int index) => QuestEligibilityTests.Quest(id) with
        {Grade="[side]",MinimumLevel=level,RewardType="[slot expansion]",Rewards=[new(0,index,null)]};
    static StoryQuest Awake(int id,int level,int stage,int prerequisite) => QuestEligibilityTests.Quest(id) with
        {Grade=stage==1 ? "[epic]" : "[achievement]",MinimumLevel=level,RewardType="[awakening type]",Rewards=[new(0,stage,null)],
            Definition=new([]) {Job="[swordman]",GrowType=1,JobChange=stage+1,PrerequisiteGroups=[[prerequisite]]}};
    static ActiveMainQuest Active(ushort id)=>new(0,id,0){AcceptanceId=Guid.NewGuid()};
    static void Consume(QuestOrdinaryCompletionTests.Fixture f,Character current)
    {
        var item=current.Inventory.Items.Single(row=>row.Core.ItemId==10164759);
        f.Repository.CommitInventory(f.Owner,current,InventoryDeleteService.Plan(current.Inventory,item.Position,item.Core.Value,item.Core.Count).Plan!);
    }
    static bool Same(Character a,Character b)=>QuestCompletionRepositoryTests.Same(a,b);
    static long Scalar(QuestOrdinaryCompletionTests.Fixture f,string sql)
    {using var db=f.Open();using var command=db.CreateCommand();command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());}
    sealed class Output:MemoryStream
    {
        public List<byte[]> Frames {get;}=[];
        public IEnumerable<ushort> Opcodes=>Frames.Select(row=>BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(1)));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)
        {Frames.Add(buffer.ToArray());return ValueTask.CompletedTask;}
    }
}
