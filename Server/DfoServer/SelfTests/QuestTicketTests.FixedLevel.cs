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
    static async Task RunFixedLevelTicketAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        SkillLearningCatalog skills,CharacterGrowthCatalog growth,QuestRewardParameterCatalog parameters)
    {
        int checks=Count;
        var raw=QuestEligibilityTests.Quest(100);
        StoryQuest[] definitions=[
            raw with {MinimumLevel=10},
            raw with {Id=101,Path="fixture/101.qst",MinimumLevel=80,RewardType="[slot expansion]",Rewards=[new(0,2,null)]},
            raw with {Id=102,Path="fixture/102.qst",MinimumLevel=90,Condition="[question]"},
            raw with {Id=103,Path="fixture/103.qst",MinimumLevel=91},
            raw with {Id=200,Path="fixture/200.qst",MinimumLevel=85,Grade="[achievement]",RewardType="[title]",Rewards=[new(0,26596,null),new(0,1,null)]},
            raw with {Id=201,Path="fixture/201.qst",MinimumLevel=20,Grade="[achievement]"},
            raw with {Id=202,Path="fixture/202.qst",MinimumLevel=20,Grade="[mission]"},
            raw with {Id=203,Path="fixture/203.qst",MinimumLevel=91,Grade="[achievement]",RewardType="[title]",Rewards=[new(0,26598,null),new(0,1,null)]}
        ];
        var catalog=new QuestCatalog(definitions.ToDictionary(row=>row.Id));
        var definition=items.Items.QuestTicket(10301410);
        That(definition.ActionIndex==182 && definition.CompletionLevel==90 && items.Items.QuestTicket(10301407) is
            {ActionIndex:181,CompletionLevel:15},"真实181/182动作从资源读取各自截止等级，不当作升级目标或通用77");
        var role=new Character(1,0,"Fixed",0,new(1,0,1,1),null)
            {Level=1,Inventory=new([new(10,Bag(65),new(10301410,ItemKind.Consumable,1))])};
        var source=QuestTicketSource.Create(role,Bag(65),definition,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        That(QuestQuickClearPlan.TryCreateTicket(role,Guid.NewGuid(),catalog,source,ImmutableDictionary<int,byte>.Empty,out var low,out _) &&
            low!.ScopeQuestIds.SequenceEqual([100,101,102,200]) && low.CutoffLevel==90 && low.TargetLevel==1 && low.PendingQuestions.IsEmpty,
            "182允许Lv1直接标记至90的主线及严格称号任务，保留角色等级、不造问答或清mission");
        var epicDefinition=definition with {ActionIndex=181};
        That(QuestQuickClearPlan.TryCreateTicket(role,Guid.NewGuid(),catalog,
            QuestTicketSource.Create(role,Bag(65),epicDefinition,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),ImmutableDictionary<int,byte>.Empty,out var epic,out _) &&
            epic!.ScopeQuestIds.SequenceEqual([100,101,102]) && epic.GeneralTitleQuestIds.IsEmpty,
            "同一资源截止等级下181只标记epic，不借182范围发称号或general奖励");
        string root=Path.Combine(Path.GetTempPath(),"S5A9-fixed-level-ticket-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var log=new ServerLog(root);
            var realQuests=SelfTestCatalogs.Quests(pvf); var titles=new TitleCatalog(pvf,realQuests);
            var titleService=new TitleAchievementService(titles,realQuests,items.Items,items.GoldLimits,items.EpicPieces);
            foreach (var (template,action,scope) in new (int,uint,int[])[] { (10301410,182,[100,101,102,200]),(10301407,181,[100,102]) })
            using (var f=new QuestOrdinaryCompletionTests.Fixture(towns,characters,skills,growth,parameters,items,definitions[0],
                definitions:definitions,active:[Active(0,100),Active(1,202)],
                questEffects:new(items.Items,skills,items.GoldLimits,items.EpicPieces,titleService),configure:candidate=>candidate with
                {
                    Level=action==181 ? (byte)16 : (byte)20,
                    InitialLearning=SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,action==181 ? (byte)16 : (byte)20,
                        candidate.InitialLearning with {Experience=growth.GetLevelStartExperience(action==181 ? (byte)16 : (byte)20)},skills),
                    Inventory=candidate.Inventory with {Items=candidate.Inventory.Items.Add(new(0,Bag(65),
                        InventoryRewardGrantService.Create(items.Items.Get(template)) with {ExpireTime=0}))}
                }))
            {
                f.Session.ReturnToRoster();
                f.Sql("INSERT INTO character_completed_quests(character_id,quest_id,answer) SELECT id,102,1 FROM characters");
                f.Session.Select(0); f.Session.MarkReady();
                using var output=new ControlledOutput(); using var stop=new CancellationTokenSource();
                await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
                var context=Context(f,output,sender,stop); var commands=new GameCommandRegistry(items.Items);
                new StackableActionHandler(f.Catalog,items.Items,new QuestProjection(f.Catalog),log).Register(commands); commands.Seal();
                var before=f.Read();
                f.Sql("CREATE TRIGGER fixed_ticket_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'fixed ticket rollback'); END");
                await RejectAsync<SqliteException>(()=>commands.DispatchAsync(context,Use(65,action)),"定级清理末段SQL失败");
                That(output.Frames.Count==0 && JsonSerializer.Serialize(f.Read())==JsonSerializer.Serialize(before),"181/182的完成位、称号和扣源随晚SQL失败整体回滚");
                f.Sql("DROP TRIGGER fixed_ticket_abort");
                await commands.DispatchAsync(context,Use(65,action)); var after=f.Read();
                That(after.CompletedQuests.SequenceEqual(scope) && after.QuestAnswers[102]==1 && after.Level==before.Level &&
                    after.GrowType==before.GrowType && after.EquipmentSlotFlags==before.EquipmentSlotFlags &&
                    JsonSerializer.Serialize(after.Learning)==JsonSerializer.Serialize(before.Learning) &&
                    after.ActiveMainQuests.Select(row=>(int)row.QuestId).SequenceEqual([202]) && after.Inventory.Find(Bag(65)) is null,
                    "固定等级券直接写资源范围完成，保留已有回答、mission、等级、职业技能及装备栏");
                Bytes(output.Frames[0].AsSpan(16),new byte[] {1,65,0,0,(byte)action,0,0,0},"181/182各自使用公共8B消费ACK，没有奖励尾段或重复消费");
                That(action==182 ? after.TitleProgress.Count==98 && after.TitleProgress.Values.All(row=>row.Remaining.Complete) &&
                    after.Inventory.Items.Count(row=>row.Core.ItemId==26596)==1 : after.TitleProgress.IsEmpty &&
                    !after.Inventory.Items.Any(row=>row.Core.ItemId==26596),"182同时发严格成就称号及general且不复制已有实体；181不发任何称号");
                output.Frames.Clear(); await commands.DispatchAsync(context,Use(65,action));
                That(!output.Opcodes.Contains((ushort)515) && JsonSerializer.Serialize(f.Read())==JsonSerializer.Serialize(after),"固定等级券重放只回绝对状态，不再登记或发奖");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(JsonSerializer.Serialize(f.Session.Selected)==JsonSerializer.Serialize(after),"重选恢复定级完成、回答和原UID，不产生升级或职业变化");
            }
        }
        finally { TestFiles.DeleteDirectory(root,true); }
        Console.WriteLine($"PASS {Count-checks} 181/182资源等级、越级标记、范围隔离、称号及原子恢复断言");
    }
}
