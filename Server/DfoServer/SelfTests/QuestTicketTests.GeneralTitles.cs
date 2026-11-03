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
    static async Task RunGeneralTitlesAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        SkillLearningCatalog skills,CharacterGrowthCatalog growth,QuestRewardParameterCatalog parameters)
    {
        int checks=Count;
        var quests=SelfTestCatalogs.Quests(pvf);
        var titles=new TitleCatalog(pvf,quests);
        var general=titles.Slots.Where(row=>row.Key.Category==0 && row.Value.Kind!=-1 && row.Value.QuestId>0)
            .OrderBy(row=>row.Key.Index).ToArray();
        var ids=general.Select(row=>row.Value.QuestId).Distinct().ToArray();
        var service=new TitleAchievementService(titles,quests,items.Items,items.GoldLimits,items.EpicPieces);
        That(ids.Length==98 && general.All(row=>row.Value.Items.Length==1 && titles.Quests.ContainsKey(row.Value.QuestId)),
            "当前PVF的general有效98项按称号簿目录读取，关闭格和其他分类不进入券奖励");
        string root=Path.Combine(Path.GetTempPath(),"S5A9-general-title-ticket-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var log=new ServerLog(root);
            using (var f=Fixture(full:true,existing:TitleBookService.Slot(0,0)))
            using (var output=new ControlledOutput())
            using (var stop=new CancellationTokenSource())
            {
                await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
                var context=Context(f,output,sender,stop);
                var commands=new GameCommandRegistry(items.Items);
                new StackableActionHandler(f.Catalog,items.Items,new QuestProjection(f.Catalog),log).Register(commands);
                commands.Seal();
                var before=f.Read();
                f.Sql("CREATE TRIGGER fail_general_titles AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'general titles rollback'); END");
                await RejectAsync<SqliteException>(()=>commands.DispatchAsync(context,Use(65,99)),"general已写进度、UID和章节邮件后故障");
                That(JsonSerializer.Serialize(f.Read())==JsonSerializer.Serialize(before) && output.Frames.Count==0 &&
                    Scalar(f,"SELECT count(*) FROM character_title_progress")==0 && Scalar(f,"SELECT count(*) FROM mailbox_messages")==0,
                    "general全组、章节邮件和扣券随晚SQL失败全部回滚，不提前ACK");
                f.Sql("DROP TRIGGER fail_general_titles");
                await commands.DispatchAsync(context,Use(65,99));
                var after=f.Read();
                That(after.Revision==before.Revision+1 && after.Inventory.Find(Bag(65)) is null &&
                    after.CompletedQuests.SequenceEqual(before.CompletedQuests) && after.ActiveMainQuests.SequenceEqual(before.ActiveMainQuests) &&
                    ids.All(id=>after.TitleProgress[id].Remaining.Complete) && after.TitleProgress.Count==ids.Length &&
                    general.All(row=>after.Inventory.Find(TitleBookService.Slot(0,row.Key.Index)) is { Uid:>0 } item &&
                        item.Core.ItemId==row.Value.Items[0]) && after.TitleSections.All(key=>key is >=0 and <256),
                    "严格任务交集为空且装备页与快捷栏满，仍只扣一券并完成general全部称号，不清普通任务或其他分类");
                That(after.Inventory.Find(TitleBookService.Slot(0,0))==before.Inventory.Find(TitleBookService.Slot(0,0)) &&
                    after.Inventory.Items.Where(item=>!TitleBookService.IsBook(item.Position.List)).SequenceEqual(
                        before.Inventory.Items.Where(item=>!TitleBookService.IsBook(item.Position.List) && item.Position!=Bag(65))),
                    "保留原称号UID与完整属性，其他库存不动，不把98件称号挤入普通背包");
                var opcodes=output.Opcodes.ToArray();
                var progress=output.Frames.Single(frame=>System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1))==359);
                var books=output.Frames.Where(frame=>System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1))==358).ToArray();
                That(opcodes.Take(7).SequenceEqual(new ushort[] {515,359,358,358,358,358,358}) && !opcodes.Contains((ushort)14) &&
                    progress.Length==16+4+ids.Length*10+16 && BitConverter.ToInt32(progress,16)==ids.Length &&
                    books.Length==5 && BitConverter.ToInt32(books[0],19)==0 && BitConverter.ToInt32(books[0],23)==ids.Length,
                    "真实515成功ACK后发送359三计数和五类358；称号簿不使用NOTI14或伪造消费尾段");
                var receipt=f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,99)!.Commit!;
                That(receipt.Duplicate && receipt.Plan.ScopeQuestIds.IsEmpty && receipt.Plan.GeneralTitleQuestIds.SequenceEqual(ids) &&
                    f.Repository.CommitQuestQuickClear(f.Owner,receipt.Plan) is { Duplicate:true,ProjectionCurrent:true } &&
                    JsonSerializer.Serialize(f.Read())==JsonSerializer.Serialize(after),
                    "general使用独立冻结范围及同一持久回执，重复恢复不重扣、不重发称号或章节邮件");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(JsonSerializer.Serialize(f.Session.Selected)==JsonSerializer.Serialize(after),"重选恢复称号UID、全部进度及章节领取状态");
                output.Frames.Clear();
                await commands.DispatchAsync(context,Use(66,99));
                That(output.Opcodes.SequenceEqual(new ushort[] {515}) && output.Frames[0][16]==0 &&
                    JsonSerializer.Serialize(f.Read())==JsonSerializer.Serialize(after),"全部general已发且无严格任务目标，下一张券拒绝并保留");
            }
            using (var f=Fixture(strict:true))
            {
                var result=f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,99)!;
                That(result.Commit is { Duplicate:false } && result.Commit.Plan.ScopeQuestIds.SequenceEqual([100,106]) &&
                    ids.All(id=>result.Character.TitleProgress[id].Remaining.Complete) &&
                    new[] {26596,26598}.All(id=>result.Character.Inventory.Items.Count(item=>item.Core.ItemId==id)==1) &&
                    result.Character.Inventory.Items.Count(item=>TitleBookService.IsBook(item.Position.List))==ids.Length-2,
                    "严格achievement/title任务与general可同次提交；已由任务获得的称号不在簿内再造副本");
            }
            using (var f=Fixture(existing:new(InventoryListType.Equipment,13)))
            {
                var before=f.Read();
                var after=f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,99)!.Commit!.Character;
                That(after.Inventory.Find(new(InventoryListType.Equipment,13))==before.Inventory.Find(new(InventoryListType.Equipment,13)) &&
                    after.Inventory.Find(TitleBookService.Slot(0,0)) is null &&
                    after.Inventory.Items.Count(item=>item.Core.ItemId==general[0].Value.Items[0])==1 && ids.All(id=>after.TitleProgress[id].Remaining.Complete),
                    "已穿戴称号不被挪走或复制，仍完成对应general领取进度");
            }

            using (var f=Fixture(template:10162220))
            using (var output=new ControlledOutput())
            using (var stop=new CancellationTokenSource())
            {
                await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
                var context=Context(f,output,sender,stop); var commands=new GameCommandRegistry(items.Items);
                new StackableActionHandler(f.Catalog,items.Items,new QuestProjection(f.Catalog),log).Register(commands); commands.Seal();
                var before=f.Read();
                That(items.Items.QuestTicket(10162220).ActionIndex==183,"真实side quest clear保留原生183，不冒充99动作");
                await commands.DispatchAsync(context,Use(65,99));
                That(output.Frames.Single()[16]==0 && JsonSerializer.Serialize(f.Read())==JsonSerializer.Serialize(before),"183源不能伪报99使用");
                output.Frames.Clear(); await commands.DispatchAsync(context,Use(65,183));
                var after=f.Read();
                Bytes(output.Frames[0].AsSpan(16),[1,65,0,0,183,0,0,0],"183成功ACK使用自身动作号及公共8B布局");
                That(after.Inventory.Find(Bag(65)) is null && ids.All(id=>after.TitleProgress[id].Remaining.Complete) &&
                    after.CompletedQuests.SequenceEqual(before.CompletedQuests),"外传完成券沿成就称号规则发general旧奖励，不清普通mission或主线");
                output.Frames.Clear(); await commands.DispatchAsync(context,Use(65,183));
                That(!output.Opcodes.Contains((ushort)515) && JsonSerializer.Serialize(f.Read())==JsonSerializer.Serialize(after),"183耗尽回执也可恢复，不重复扣券或发称号");
            }

            QuestOrdinaryCompletionTests.Fixture Fixture(bool full=false,bool strict=false,InventorySlot? existing=null,int template=AchievementTicketId)
            {
                var definitions=AchievementDefinitions().Where(quest=>strict || !QuestQuickClearPlan.AchievementTitle(quest)).ToArray();
                return new(towns,characters,skills,growth,parameters,items,definitions[0],materialCount:0,definitions:definitions,active:[],
                    fullEquipment:full,questEffects:new(items.Items,skills,items.GoldLimits,items.EpicPieces,service),configure:candidate=>
                    {
                        var inventory=candidate.Inventory with { Items=candidate.Inventory.Items.AddRange(new InventoryItem[] {
                            new(0,Bag(65),InventoryRewardGrantService.Create(items.Items.Get(template)) with { ExpireTime=0 }),
                            new(0,Bag(66),InventoryRewardGrantService.Create(items.Items.Get(AchievementTicketId)) with { ExpireTime=0 }) }) };
                        if (existing is { } slot) inventory=inventory with { Items=inventory.Items.Add(new(0,slot,
                            InventoryRewardGrantService.Create(items.Items.Get(general[0].Value.Items[0])) with { SortLockFlag=1 })) };
                        if (full)
                            for (short quickSlot=ItemSlotBoundService.QuickSlotStart;quickSlot<=ItemSlotBoundService.QuickSlotEnd;quickSlot++)
                                inventory=inventory with { Items=inventory.Items.Add(new(0,Bag(quickSlot),InventoryRewardGrantService.Create(items.Items.Get(10403)))) };
                        return candidate with { Level=20,CompletedQuests=[110],Inventory=inventory,InitialLearning=SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,20,
                            candidate.InitialLearning with { Experience=growth.GetLevelStartExperience(20) },skills) };
                    });
            }
        }
        finally { TestFiles.DeleteDirectory(root,true); }
        Console.WriteLine($"PASS {Count-checks} general全组、完整称号簿帧、保留实体、空背包容量及原子恢复断言");
    }
}
