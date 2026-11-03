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
    static async Task RunMissionTicketAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        SkillLearningCatalog skills,CharacterGrowthCatalog growth,QuestRewardParameterCatalog parameters)
    {
        int checks=Count;
        var definitions=new[] {
            QuestEligibilityTests.Quest(201) with { Grade="[mission]",MinimumLevel=20,Definition=new([]) { PrerequisiteGroups=[[200]] } },
            QuestEligibilityTests.Quest(200) with { Grade="[mission]",MinimumLevel=20,Definition=new([]) { PrerequisiteGroups=[[100]] } },
            QuestEligibilityTests.Quest(100) with { MinimumLevel=19 },
            QuestEligibilityTests.Quest(102) with { Grade="[normal]",MinimumLevel=20 },
            QuestEligibilityTests.Quest(202) with { Grade="[mission]",MinimumLevel=21 },
            QuestEligibilityTests.Quest(203) with { Grade="[side]",MinimumLevel=20 }
        };
        var catalog=new QuestCatalog(definitions.ToDictionary(row=>row.Id));
        var role=new Character(1,0,"Mission",0,new(1,0,1,1),null)
            { Level=20,Inventory=new([new(10,Bag(65),new(TicketId,ItemKind.Consumable,1))]) };
        var source=QuestTicketSource.Create(role,Bag(65),items.Items.QuestTicket(TicketId),DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        That(QuestQuickClearPlan.TryCreateTicket(role,Guid.NewGuid(),catalog,source,ImmutableDictionary<int,byte>.Empty,out var mixed,out _) &&
            mixed!.ScopeQuestIds.SequenceEqual([100,102,200,201]),"77在同一次范围保留epic/normal并加入当前等级mission，按真实前置闭合而非仅完成mission");
        That(QuestQuickClearPlan.TryCreateManual(role,Guid.NewGuid(),catalog,ImmutableDictionary<int,byte>.Empty,out var manual,out _) &&
            manual!.ScopeQuestIds.SequenceEqual([100]),"普通手册仍只到低一级epic，不因任务券扩展清掉mission");

        var quests=SelfTestCatalogs.Quests(pvf);
        var missions=quests.All.Where(row=>row.Grade=="[mission]" && row.MinimumLevel<=83).Select(row=>row.Id).Order().ToArray();
        var completed=quests.All.Where(row=>row.Grade is null or "" or "[epic]" or "[normal]" && row.MinimumLevel<=83)
            .Select(row=>row.Id).Order().ToArray();
        That(missions.Length==32,"匹配封包Lv83范围内有32项mission，复现主线已清但mission仍未完成的1430路径");
        string root=Path.Combine(Path.GetTempPath(),"S5A9-mission-ticket-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var log=new ServerLog(root);
            using var f=new QuestOrdinaryCompletionTests.Fixture(towns,characters,skills,growth,parameters,items,quests.Get(7926),
                definitions:quests.All.ToArray(),active:[Active(0,7926),Active(1,7932),Active(2,4391)],
                questEffects:new(items.Items,skills,items.GoldLimits,items.EpicPieces),configure:candidate=>
                {
                    var created=CharacterCreation.Prepare(candidate.Name,characters.Get(2),towns.Room,towns.InitialReturn,skills.Get);
                    return created with { Level=83,CompletedQuests=completed,
                        InitialLearning=SkillPointLedger.Synchronize(created.Job,created.GrowType,83,
                            created.InitialLearning with { Experience=growth.GetLevelStartExperience(83) },skills),
                        Inventory=created.Inventory with { Gold=12345,Items=created.Inventory.Items.AddRange(new InventoryItem[] {
                            new(0,Bag(65),InventoryRewardGrantService.Create(items.Items.Get(TicketId)) with { ExpireTime=0 }),
                            new(0,Bag(66),InventoryRewardGrantService.Create(items.Items.Get(TicketId)) with { ExpireTime=0 }) }) } };
                });
            using var output=new ControlledOutput(); using var stop=new CancellationTokenSource();
            await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
            var context=Context(f,output,sender,stop); var projection=new QuestProjection(f.Catalog);
            var commands=new GameCommandRegistry(items.Items);
            new QuestQuickClearHandler(f.Catalog,QuestConnectionCatalog.Parse([],_=>""),projection,log,items.Items).Register(commands);
            new StackableActionHandler(f.Catalog,items.Items,projection,log).Register(commands); commands.Seal();
            var before=f.Read();
            await commands.DispatchAsync(context,Prepare());
            That(output.Opcodes.SequenceEqual(new ushort[] {1430}) && output.Frames[0][16]==1 &&
                f.Session.PrepareQuestTicket(TicketId,f.Catalog,items.Items)!.Prepared!.ScopeQuestIds.SequenceEqual(missions) &&
                JsonSerializer.Serialize(f.Read())==JsonSerializer.Serialize(before),"真实10006007在仅剩mission时1430成功准备完整范围，但不提前扣券或完成");
            output.Frames.Clear();
            f.Sql("CREATE TRIGGER mission_ticket_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'mission ticket rollback'); END");
            await RejectAsync<SqliteException>(()=>commands.DispatchAsync(context,Use(65)),"mission券末段SQL失败");
            That(output.Frames.Count==0 && JsonSerializer.Serialize(f.Read())==JsonSerializer.Serialize(before),"mission完成位、清槽与扣券一起回滚，不提前ACK");
            f.Sql("DROP TRIGGER mission_ticket_abort");
            await commands.DispatchAsync(context,Use(65));
            var after=f.Read();
            That(output.Opcodes.First()==515 && output.Opcodes.TakeLast(3).SequenceEqual(new ushort[] {574,21,356}) &&
                after.CompletedQuests.SequenceEqual(completed.Concat(missions).Order()) && after.Revision==before.Revision+1 &&
                after.Inventory.Find(Bag(65)) is null && after.Inventory.Find(Bag(66))==before.Inventory.Find(Bag(66)) &&
                after.ActiveMainQuests.Select(row=>(int)row.QuestId).SequenceEqual([7932,4391]),
                "真实515一次提交全部32项mission，保留原主线完成、超等级mission及范围外side活动槽");
            Bytes(output.Frames.First(frame=>BitConverter.ToUInt16(frame,1)==515).AsSpan(16),[1,65,0,0,77,0,0,0],"77仍用原8B消费ACK，再发布提交后的任务状态");
            That(after.Level==before.Level && after.GrowType==before.GrowType && after.Inventory.Gold==before.Inventory.Gold &&
                JsonSerializer.Serialize(after.Learning)==JsonSerializer.Serialize(before.Learning) &&
                after.Inventory.Items.Where(item=>item.Core.Kind!=ItemKind.Quest && item.Core.ItemId!=TicketId).SequenceEqual(
                    before.Inventory.Items.Where(item=>item.Core.Kind!=ItemKind.Quest && item.Core.ItemId!=TicketId)),
                "mission沿普通跳过效果，不发普通物品经验金币，不扣普通材料或改动职业技能");
            output.Frames.Clear(); await commands.DispatchAsync(context,Use(65));
            That(!output.Opcodes.Contains((ushort)515) && JsonSerializer.Serialize(f.Read())==JsonSerializer.Serialize(after),"mission券已确认回执只恢复绝对状态，不重复消费");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady(); output.Frames.Clear();
            await commands.DispatchAsync(context,Prepare());
            That(output.Opcodes.SequenceEqual(new ushort[] {1430}) && output.Frames[0][16]==0 &&
                JsonSerializer.Serialize(f.Read())==JsonSerializer.Serialize(after),"重选保留32项mission完成；范围全部已清时1430才拒绝且保留第二张券");
        }
        finally { TestFiles.DeleteDirectory(root,true); }
        Console.WriteLine($"PASS {Count-checks} 普通77的epic/normal/mission、1430准备、515原子完成及恢复断言");
    }
}
