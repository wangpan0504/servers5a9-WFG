using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
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

namespace DfoServer;

internal static partial class QuestTicketTests
{
    const int AchievementTicketId=490001977;

    static StoryQuest[] AchievementDefinitions()
    {
        var first=QuestEligibilityTests.Quest(100) with
        {
            Grade="[achievement]",RewardType="[title]",MinimumLevel=19,
            Condition="[seeking]",IntData=[10165055,9999],DeclaredGoldMultiple=1000,
            Rewards=[new(0,26596,null),new(0,1,null),new(0,0,null),new(0,99999,null)],
            Definition=new([]) { Job="[all]",AcceptanceRewards=[new(0,3037,null),new(0,99,null)] }
        };
        return [
            first with { Id=106,Path="fixture/106.qst",Condition="[meet npc]",IntData=[1],
                Definition=new([]) { PrerequisiteGroups=[[100]] },Rewards=[new(0,26598,null),new(0,1,null)] },
            first,
            first with { Id=101,Path="fixture/101.qst",RewardType="[item]" },
            first with { Id=102,Path="fixture/102.qst",Grade="[title]" },
            first with { Id=103,Path="fixture/103.qst",Grade="[epic]" },
            first with { Id=104,Path="fixture/104.qst",MinimumLevel=21 },
            first with { Id=105,Path="fixture/105.qst",Definition=new([]) { PrerequisiteGroups=[[101]] } },
            first with { Id=107,Path="fixture/107.qst",Definition=new([]) { Job="[fighter]" } },
            first with { Id=110,Path="fixture/110.qst" }
        ];
    }

    static void RunAchievementCore()
    {
        int checks=Count;
        var raw=new StackableFile(AchievementTicketId,"stackable/achievement.stk",new("[etc]",[1]),1,0,0,1,null,[])
            { ActionDeclaration=new("[achievement quest clear]",[]),HasUseActionPacket=true,DeclaredUseActionPacket=0 };
        var template=new ItemTemplate(AchievementTicketId,ItemKind.Consumable,0,1,0,1,0,2,1,"[trade]",ushort.MaxValue,0,"Achievement");
        var definition=QuestTicketDefinition.Parse(raw,template);
        That(definition.ActionIndex==99 && definition.TargetQuestIds.IsEmpty && definition.AwakeningStage==0,
            "99成就券保留独立动作，不冒充普通、指定或觉醒券");
        That(QuestTicketDefinition.Parse(raw with { DeclaredMinimumLevel=null },template).MinimumLevel==1,
            "合法缺省最低等级复用物品模板，不把空声明当成额外门禁");
        TestCheck.Reject<NotSupportedException>(()=>QuestTicketDefinition.Parse(raw with
            { ActionDeclaration=new("[achievement quest clear]",[1]) },template),"未知99参数不能被忽略后执行");
        var role=new Character(1,0,"Achieve",0,new(1,0,1,1),null)
        {
            Level=20,CompletedQuests=[110],Inventory=new([new(10,Bag(65),new(AchievementTicketId,ItemKind.Consumable,2))]),
            ActiveMainQuests=[Active(0,100),Active(1,101),Active(2,102),Active(3,103)]
        };
        var catalog=new QuestCatalog(AchievementDefinitions().ToDictionary(quest=>quest.Id));
        var source=QuestTicketSource.Create(role,Bag(65),definition,100);
        string before=JsonSerializer.Serialize(role);
        That(QuestQuickClearPlan.TryCreateTicket(role,Guid.NewGuid(),catalog,source,ImmutableDictionary<int,byte>.Empty,out var plan,out _) &&
            plan is { IsAchievementTicket:true } && plan.ScopeQuestIds.SequenceEqual([100,106]) &&
            plan.CompletedAfter.SequenceEqual([100,106,110]) && plan.ActiveAfter.Select(row=>row.QuestId).SequenceEqual(new ushort[] {101,102,103}) &&
            plan.Ticket!.Inventory.After.Find(Bag(65))!.Core.Count==1 && JsonSerializer.Serialize(role)==before,
            "99只完成achievement/title交集及其已满足前置的目标，不清其它成就、称号簿、主线或未来等级任务");
        using (var snapshot=JsonDocument.Parse(plan!.Snapshot))
            That(snapshot.RootElement.GetProperty("Action").GetUInt32()==99,"99动作随冻结回执保存，不改变77历史快照");
        var empty=new QuestCatalog(AchievementDefinitions().Where(quest=>!QuestQuickClearPlan.AchievementTitle(quest)).ToDictionary(quest=>quest.Id));
        That(!QuestQuickClearPlan.TryCreateTicket(role,Guid.NewGuid(),empty,source,ImmutableDictionary<int,byte>.Empty,out _,out _) &&
            !QuestQuickClearPlan.TryCreateTicket(role with { CompletedQuests=[100,106,110] },Guid.NewGuid(),catalog,
                QuestTicketSource.Create(role with { CompletedQuests=[100,106,110] },Bag(65),definition,100),ImmutableDictionary<int,byte>.Empty,out _,out _),
            "交集为空或合法目标均已完成时不消费，不扩大到其它奖励任务");
        Bytes(StackableActionPackets.Used(new(65,0,0,99,0,0)),[1,65,0,0,99,0,0,0],"99沿本版公共8B成功消费，未新增尾段");
        Console.WriteLine($"PASS {Count-checks} 成就99严格双标签、前置、空目标与完整协议断言");
    }

    static async Task RunAchievementAsync(TownCatalog towns,CharacterCatalog characters,ItemResources items,
        SkillLearningCatalog skills,CharacterGrowthCatalog growth,QuestRewardParameterCatalog parameters)
    {
        int checks=Count;
        That(items.Items.QuestTicket(AchievementTicketId).ActionIndex==99 &&
            new[] {26596,26598}.All(id=>items.Items.Get(id) is { Kind:ItemKind.Equipment,EquipmentSlot:13 }),
            "复用真实99券和称号模板，正向任务在隔离目录合成，不修改实际PVF或玩家库");
        string root=Path.Combine(Path.GetTempPath(),"S5A9-achievement-ticket-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var log=new ServerLog(root);
            using (var f=Fixture(AchievementDefinitions()))
            using (var output=new ControlledOutput())
            using (var stop=new CancellationTokenSource())
            {
                await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
                var context=Context(f,output,sender,stop); var commands=Registry(f); var before=f.Read();
                f.Sql("CREATE TRIGGER fail_achievement_title BEFORE INSERT ON character_quest_effect_receipts BEGIN SELECT RAISE(ABORT,'achievement title rollback'); END");
                await RejectAsync<SqliteException>(()=>commands.DispatchAsync(context,Use(65,99)),"99晚回执故障不能提前成功");
                That(JsonSerializer.Serialize(f.Read())==JsonSerializer.Serialize(before) && output.Frames.Count==0,
                    "扣券、称号入包和完成位已写后的晚失败全部回滚，未发成功ACK");
                f.Sql("DROP TRIGGER fail_achievement_title");
                await commands.DispatchAsync(context,Use(65,99));
                var after=f.Read();
                That(output.Opcodes.SequenceEqual(new ushort[] {515,14,574,21,356}) &&
                    after.CompletedQuests.SequenceEqual([100,106,110]) && after.Revision==before.Revision+1 &&
                    after.Inventory.Find(Bag(65)) is null && after.Inventory.Find(Bag(66))==before.Inventory.Find(Bag(66)) &&
                    new[] {26596,26598}.All(id=>after.Inventory.Items.Single(item=>item.Core.ItemId==id) is { Uid:>0,Core.Count:1 }),
                    "99经真实Handler提交后只扣指定一张并发两件真实称号，8B消费ACK后发布奖励和任务状态");
                Bytes(output.Frames[0].AsSpan(16),[1,65,0,0,99,0,0,0],"99成功回包是完整8B而非假成功或其它动作");
                That(Stable(before)==Stable(after) && JsonSerializer.Serialize(after.Learning)==JsonSerializer.Serialize(before.Learning) && after.Level==before.Level &&
                    after.GrowType==before.GrowType && after.EquipmentSlotFlags==before.EquipmentSlotFlags,
                    "不发金币经验及普通物品，不扣寻物材料，不改职业、装备栏或其它库存状态");
                var receipt=f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,99)!.Commit!;
                That(receipt.Duplicate && f.Repository.CommitQuestQuickClear(f.Owner,receipt.Plan).Duplicate &&
                    JsonSerializer.Serialize(f.Read())==JsonSerializer.Serialize(after) && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts")==1,
                    "提交后同操作回放恢复原称号UID，不重扣源、不重发资产或多记完成");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(JsonSerializer.Serialize(f.Session.Selected)==JsonSerializer.Serialize(after),"重选读取原称号实例及完成记录，不补奖");
            }
            using (var f=Fixture(AchievementDefinitions(),full:true))
            {
                var before=JsonSerializer.Serialize(f.Read());
                That(f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,99) is { Commit:null } && JsonSerializer.Serialize(f.Read())==before,
                    "装备页及快捷栏都满时称号批量发放失败，不清任务或扣券");
            }
            using (var f=Fixture(AchievementDefinitions().Where(quest=>!QuestQuickClearPlan.AchievementTitle(quest)).ToArray()))
            {
                var before=JsonSerializer.Serialize(f.Read());
                That(f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,99) is { Commit:null } && JsonSerializer.Serialize(f.Read())==before,
                    "真实券遇到空交集也不消费，不借其它achievement或title分类任务凑成功");
            }
            using (var f=Fixture(AchievementDefinitions(),template:490001424,count:2))
            using (var output=new ControlledOutput())
            using (var stop=new CancellationTokenSource())
            {
                await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
                var context=Context(f,output,sender,stop); var commands=Registry(f);
                await commands.DispatchAsync(context,Use(65,99));
                var after=JsonSerializer.Serialize(f.Read()); output.Frames.Clear();
                await commands.DispatchAsync(context,Use(65,99));
                That(output.Opcodes.SequenceEqual(new ushort[] {515}) && output.Frames[0][16]==0 &&
                    f.Read().Inventory.Find(Bag(65)) is { Core.Count:1 } && JsonSerializer.Serialize(f.Read())==after,
                    "99确认成功后的同槽剩余券会按当前范围判断，无新目标正常失败，不吞新点击或重复消费");
            }

            QuestOrdinaryCompletionTests.Fixture Fixture(StoryQuest[] definitions,bool full=false,int template=AchievementTicketId,int count=1) =>
                new(towns,characters,skills,growth,parameters,items,definitions[0],materialCount:5,definitions:definitions,
                    active:definitions.Where(quest=>quest.Id is 100 or 101 or 103).Select((quest,index)=>Active((byte)index,(ushort)quest.Id)).ToArray(),
                    fullEquipment:full,questEffects:new(items.Items,skills,items.GoldLimits,items.EpicPieces),
                    configure:candidate=>
                    {
                        var inventory=candidate.Inventory with { Gold=1234,Items=candidate.Inventory.Items.AddRange(new InventoryItem[] {
                            new(0,Bag(65),InventoryRewardGrantService.Create(items.Items.Get(template),count) with { ExpireTime=0,SortLockFlag=1 }),
                            new(0,Bag(66),InventoryRewardGrantService.Create(items.Items.Get(AchievementTicketId)) with { ExpireTime=0 }) }) };
                        if (full)
                            for (short slot=ItemSlotBoundService.QuickSlotStart;slot<=ItemSlotBoundService.QuickSlotEnd;slot++)
                                if (inventory.Find(Bag(slot)) is null)
                                    inventory=inventory with { Items=inventory.Items.Add(new(0,Bag(slot),InventoryRewardGrantService.Create(items.Items.Get(10403)))) };
                        return candidate with { Level=20,CompletedQuests=[110],Inventory=inventory,
                            InitialLearning=SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,20,
                                candidate.InitialLearning with { Experience=growth.GetLevelStartExperience(20) },skills) };
                    });
            GameCommandRegistry Registry(QuestOrdinaryCompletionTests.Fixture f)
            {
                var commands=new GameCommandRegistry(items.Items);
                new StackableActionHandler(f.Catalog,items.Items,new QuestProjection(f.Catalog),log).Register(commands);
                commands.Seal(); return commands;
            }
            static string Stable(Character character) => JsonSerializer.Serialize(character.Inventory with
                { Items=character.Inventory.Items.Where(item=>item.Core.ItemId is not (AchievementTicketId or 26596 or 26598)).ToImmutableArray() });
        }
        finally { TestFiles.DeleteDirectory(root,true); }
        Console.WriteLine($"PASS {Count-checks} 成就99称号事务、515帧、晚回滚、空交集及重登恢复断言");
    }
}
