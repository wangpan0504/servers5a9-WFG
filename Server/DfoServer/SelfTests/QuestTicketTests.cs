using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
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
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestTicketTests
{
    const int TicketId = 10006007;
    static InventorySlot Bag(short slot) => new(InventoryListType.Main,slot);

    public static void RunCore()
    {
        int checks = Count;
        var packet = Use(65);
        That(QuestTicketRequests.TryUse(packet.Body,out var request) && request == new QuestTicketUseRequest(65,0,0,77,0,0) &&
            !QuestTicketRequests.TryUse(packet.Body.AsSpan(0,18),out _) && !QuestTicketRequests.TryUse([.. packet.Body,0],out _),
            "515精确19B，slot/list/reserved/action/两个参数独立解码");
        var wide = Use(ushort.MaxValue,uint.MaxValue,uint.MaxValue,uint.MaxValue,uint.MaxValue,byte.MaxValue);
        That(QuestTicketRequests.TryUse(wide.Body,out var decoded) && decoded.Slot == ushort.MaxValue && decoded.List == byte.MaxValue &&
            decoded.Reserved == uint.MaxValue && decoded.Action == uint.MaxValue && decoded.FirstArgument == uint.MaxValue &&
            decoded.SecondArgument == uint.MaxValue,"parser保留完整整数，不把动作或物品上下文解释为任务ID");
        Bytes(QuestTicketPackets.Used(request),[1,65,0,0,77,0,0,0],"77成功ACK515固定8B，不追加消耗数量");
        Bytes(QuestTicketPackets.Rejected(request,0x1234),[0,0x34,0x12,65,0,0,77],"失败ACK515的u16错误及u8动作符合本版消费者");
        var definition = new QuestTicketDefinition(TicketId,0,84,ushort.MaxValue,new());
        var item = new InventoryItem(10,Bag(65),new(TicketId,ItemKind.Consumable,2));
        var role = new Character(1,0,"Ticket",0,new(1,0,1,1),null)
        {
            Level=20,Inventory=new([item]),ActiveMainQuests=[Active(0,400,0)]
        };
        var quests = new QuestCatalog(PlainDefinitions().ToDictionary(row => row.Id));
        var source = QuestTicketSource.Create(role,item.Position,definition,100);
        That(QuestQuickClearPlan.TryCreateTicket(role,Guid.NewGuid(),quests,source,ImmutableDictionary<int,byte>.Empty,out var plan,out string reason) &&
            plan is { IsTicket:true,IsManual:false,CutoffLevel:20,AcceptanceId:var empty,ParentKey:null,QuestionQuestId:null } &&
            empty == Guid.Empty && plan.ScopeQuestIds.SequenceEqual([100,101,102]) && plan.Ticket!.Inventory.After.Find(Bag(65))!.Core.Count == 1,
            "普通券包含当前等级epic/normal，独立操作身份并只消费一张: "+reason);
        That(plan!.ActiveAfter.Single(row => row.QuestId == 400).ConditionState == 1 &&
            QuestQuickClearPlan.TryCreateManual(role,Guid.NewGuid(),quests,ImmutableDictionary<int,byte>.Empty,out var manual,out _) &&
            manual!.ScopeQuestIds.SequenceEqual([100]),"消费后未跳过的高等级寻物重算0→1；手册截止不冒充券范围");
        foreach (byte job in Enumerable.Range(0,16).Select(value => (byte)value))
            That(definition.Allows(20,job),"普通券资源all职业资格 "+job);
        That(!definition.Allows(85,0) && !(definition with { MinimumLevel=21 }).Allows(20,0) &&
            !(definition with { Jobs=2 }).Allows(20,0),"券自身min/max/job范围不由角色或任务编号猜测");
        TestCheck.Reject<InvalidDataException>(() => QuestTicketSource.Create(role with
            { Inventory=new([item with { Core=item.Core with { ExpireTime=100 } }]) },Bag(65),definition,100),"到期实际实例不能准备");
        TestCheck.Reject<InvalidDataException>(() => QuestTicketSource.Create(role with
            { Inventory=new([item with { Core=item.Core with { EquipmentLockId=1 } }]) },Bag(65),definition,100),"锁定券不能消费");
        var questions = new QuestCatalog(QuestionDefinitions().ToDictionary(row => row.Id));
        That(QuestQuickClearPlan.TryCreateTicket(role,Guid.NewGuid(),questions,source,ImmutableDictionary<int,byte>.Empty,out var first,out _) &&
            first!.PendingQuestions.SequenceEqual([100]) && first.ScopeQuestIds.Length == 0,"券问题只准备，不默认选择或扣券");
        That(QuestQuickClearPlan.TryCreateTicket(role,first!.OperationId,questions,source,ImmutableDictionary<int,byte>.Empty.Add(100,1),out var answered,out _) &&
            answered!.ScopeQuestIds.SequenceEqual([100,102]) && !answered.CompletedAfter.Contains(101) &&
            answered.OperationId == first.OperationId && ReferenceEquals(answered.Ticket,source),"零基答案沿原操作和source，只完成真实选择分支");
        Console.WriteLine($"PASS {Count-checks} 普通77券协议、等级范围、问题和实例消费规划断言");
    }

    public static async Task RunAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        SkillLearningCatalog? sharedSkills = null)
    {
        int checks = Count;
        var skills = sharedSkills ?? SkillLearningCatalog.Load(pvf);
        var growth = CharacterGrowthCatalog.Load(pvf,skills); var parameters = QuestRewardParameterCatalog.Load(pvf);
        var definition = items.Items.QuestTicket(TicketId);
        That(definition is { MinimumLevel:0,MaximumLevel:84,Jobs:ushort.MaxValue } && definition.Expiration.AbsoluteTime > 0,
            "匹配10006007真实普通77券，完整保留活动期限元数据和0..84/all范围");
        foreach (int specified in new[] {10005390,10005391,490000716,10006257})
        {
            var specific = items.Items.QuestTicket(specified);
            That(specific.ActionIndex == 130 && specific.TargetQuestIds.SequenceEqual(specified == 10005390 ? new[] {675,650} : [674,649]),
                "真实130指定任务券保留原目标，不冒充普通77范围: "+specified);
        }
        string logRoot=Path.Combine(Path.GetTempPath(),"S5A9-quest-ticket-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logRoot);
        try
        {
            using var log = new ServerLog(logRoot);
            using (var f = Fixture(PlainDefinitions(),[Active(0,100),Active(1,300),Active(2,400,0)]))
            {
                var before=f.Read(); var original=before.Inventory.Find(Bag(65))!; var second=before.Inventory.Find(Bag(66));
                using var output=new ControlledOutput(); using var stop=new CancellationTokenSource();
                await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
                var context=Context(f,output,sender,stop); var commands=Registry(f);
                foreach (var invalid in new[] {Use(65,130),Use(65,119),Use(65,122),Use(65,reserved:1),Use(65,first:1),Use(65,second:1),Use(65,list:1)})
                {
                    That(await commands.DispatchAsync(context,invalid) == CommandResult.Handled && Same(before,f.Read()) &&
                        output.Opcodes.SequenceEqual(new ushort[] {515}) && output.Frames.Single()[16] == 0,
                        "未确认动作/参数/容器失败ACK，不消费或占位成功");
                    output.Frames.Clear();
                }
                That(await commands.DispatchAsync(context,Use(65)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {515,574,21,356}),"无问题真实515可独立提交；ACK515→活动→可接→完成，无NOTI14");
                Bytes(output.Frames[0].AsSpan(16),[1,65,0,0,77,0,0,0],"消费ACK不携带剩余量，客户端77自行减一");
                var after=f.Read();
                That(after.Revision == before.Revision+1 && after.CompletedQuests.SequenceEqual([100,101,102]) &&
                    after.Inventory.Find(Bag(65)) is null && after.Inventory.Find(Bag(66)) == second &&
                    after.Inventory.Items.Select(row=>row.Uid).SequenceEqual(before.Inventory.Items.Where(row=>row.Uid != original.Uid).Select(row=>row.Uid)) &&
                    Assets(before) == Assets(after) && after.ActiveMainQuests.Single(row=>row.QuestId==300).ConditionState == 0 &&
                    after.ActiveMainQuests.Single(row=>row.QuestId==400).ConditionState == 1,
                    "只移除一张真实UID券，普通奖励/材料/成长保持，父状态与保留寻物同一次revision落库");
                That(Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts WHERE operation_kind='Ticket' AND acceptance_id IS NULL AND parent_key IS NULL AND question_quest IS NULL") == 1,
                    "Ticket复用跳过回执，无假question/接取身份");
                var plan=f.Session.UseQuestTicket(65,0,f.Catalog,items.Items)!.Commit!.Plan;
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Use(65)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {574,21,356}) && Same(after,f.Read()) &&
                    f.Repository.CommitQuestQuickClear(f.Owner,plan) is { Duplicate:true,ProjectionCurrent:true },
                    "已发布ACK的重复515不重复客户端扣券，持久回执和寻物进度精确恢复");
                var later=new QuestCatalog(new() { [500]=QuestEligibilityTests.Quest(500) });
                var next=f.Session.PrepareQuestTicket(TicketId,later,items.Items)!.Prepared!;
                That(next.OperationId != plan.OperationId && next.Ticket!.Item == second && next.ScopeQuestIds.SequenceEqual([500]),
                    "显式新1430使用当前下一张source及独立操作，不复用旧已提交计划");
                That(f.Session.UseQuestTicket(66,0,later,items.Items)?.Commit is { Duplicate:false } &&
                    f.Read().Revision == after.Revision+1 && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 2,
                    "独立下一次消费仍使用同事务及独立回执");
                var persisted=f.Read(); f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(Same(persisted,f.Session.Selected!),"重选恢复消费后真实库存/完成/进度，不补奖励");
            }
            using (var f = Fixture(QuestionDefinitions(),[]))
            {
                var before=f.Read(); using var output=new ControlledOutput(); using var stop=new CancellationTokenSource();
                await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
                var context=Context(f,output,sender,stop); var commands=Registry(f);
                That(await commands.DispatchAsync(context,Use(65)) == CommandResult.Handled && output.Frames.Single()[16] == 0 && Same(before,f.Read()),
                    "有问题515不能绕过1430/真实回答直接清整链");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Prepare()) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {1381}) && Same(before,f.Read()),"非零1430按真实template找券，1381只准备问题不提前扣券");
                var original=f.Session.PrepareQuestTicket(TicketId,f.Catalog,items.Items)!.Prepared!;
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Answer(100,1)) == CommandResult.Handled && output.Opcodes.SequenceEqual(new ushort[] {1431}) &&
                    output.Frames.Single()[16] == 1 && Same(before,f.Read()),"券最终1431仅ACK，消费提交等待真实515，不套手册1431提交");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,new(1,1457,[])) == CommandResult.Handled && output.Frames.Count == 0 && Same(before,f.Read()),
                    "手册1457不能消费券或提前写完成");
                That(await commands.DispatchAsync(context,Use(66)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {515,574,21,356}) && f.Read().QuestAnswers[100] == 1 &&
                    f.Read().CompletedQuests.SequenceEqual([100,102]) && f.Read().Revision == before.Revision+1 &&
                    f.Session.UseQuestTicket(66,0,f.Catalog,items.Items)!.Commit!.Plan.OperationId == original.OperationId &&
                    f.Read().Inventory.Find(Bag(65)) == before.Inventory.Find(Bag(65)) && f.Read().Inventory.Find(Bag(66)) is null,
                    "模板准备后515绑定第二槽真实source，保留原零基答案/范围/操作，只扣所报券不借扣第一张");
            }
            foreach (bool expired in new[] {false,true})
            using (var f = Fixture(PlainDefinitions(),[],firstLocked:!expired,
                firstExpireTime:expired ? checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()-10) : null))
            {
                var before=f.Read(); using var output=new ControlledOutput(); using var stop=new CancellationTokenSource();
                await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
                var context=Context(f,output,sender,stop); var commands=Registry(f);
                That(await commands.DispatchAsync(context,Prepare()) == CommandResult.Handled && output.Opcodes.SequenceEqual(new ushort[] {1430}) &&
                    f.Session.PrepareQuestTicket(TicketId,f.Catalog,items.Items)!.Prepared!.Ticket!.Item.Position == Bag(66) && Same(before,f.Read()),
                    "首张锁定/已到期不挡同模板有效第二张准备: "+expired);
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Use(66)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {515,574,21,356}) && f.Read().Inventory.Find(Bag(66)) is null &&
                    f.Read().Inventory.Find(Bag(65)) == before.Inventory.Find(Bag(65)) && f.Read().Revision == before.Revision+1,
                    "消费实际有效source，不改变锁定首张或恢复已清除过期实例: "+expired);
            }
            using (var f = Fixture(PlainDefinitions(),[Active(0,400,0)],useCycles:true,
                expireTime:checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()+5)))
            {
                var before=f.Read(); var plan=f.Session.PrepareQuestTicket(TicketId,f.Catalog,items.Items)!.Prepared!;
                f.Sql("CREATE TRIGGER ticket_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'ticket rollback'); END");
                TestCheck.Reject<SqliteException>(()=>f.Session.UseQuestTicket(65,0,f.Catalog,items.Items),"券回执末段SQL失败");
                That(Same(before,f.Read()) && ReferenceEquals(f.Session.Selected,plan.Expected) &&
                    Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 0 &&
                    f.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1)) is null,"券/完成/寻物/答案/revision整笔回滚，实际提交待恢复保持原周期");
                var bound=f.Session.PrepareQuestTicket(TicketId,f.Catalog,items.Items)!.Prepared!;
                That(bound.OperationId==plan.OperationId && ReferenceEquals(bound.Expected,plan.Expected) && bound.Ticket!.Item.Position==Bag(65),
                    "SQL失败保留首次515已绑定的最终snapshot，不退回1430候选source或重生成操作");
                That(f.Session.UseQuestTicket(66,0,f.Catalog,items.Items) is { Commit:null } && Same(before,f.Read()),
                    "CommitStarted恢复不切换第二张source，不将失败提交重规划成另一消费");
                f.Sql("DROP TRIGGER ticket_abort");
                var committed=f.Repository.CommitQuestQuickClear(f.Owner,bound);
                await UntilExpired(bound.Ticket!.Item.Core.ExpireTime);
                var recovered=f.Session.UseQuestTicket(65,0,f.Catalog,items.Items);
                That(recovered?.Commit is { Duplicate:true,ProjectionCurrent:true } && Same(committed.Character,f.Read()) &&
                    f.Read().ActiveMainQuests.Single().ConditionState == 1 && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 1,
                    "已提交返回丢失在期限已过后仍先恢复原回执/source/Operation，恢复寻物且不重扣或重新校验消费期限");
            }
            using (var f = Fixture(PlainDefinitions(),[]))
            {
                var before=f.Read(); var commands=Registry(f);
                using (var broken=new ControlledOutput { FailOn=1 })
                using (var stop=new CancellationTokenSource())
                await using (var sender=new GameConnectionSender(f.Owner,broken,stop,0,(_,_,_,_,_)=>{}))
                    await RejectAsync<IOException>(()=>commands.DispatchAsync(Context(f,broken,sender,stop),Use(65)),"已提交后的ACK515发送失败");
                var after=f.Read(); using var output=new ControlledOutput(); using var retryStop=new CancellationTokenSource();
                await using var retrySender=new GameConnectionSender(f.Owner,output,retryStop,0,(_,_,_,_,_)=>{});
                That(await commands.DispatchAsync(Context(f,output,retrySender,retryStop),Use(65)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {515,574,21,356}) && Same(after,f.Read()) &&
                    after.Revision == before.Revision+1 && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 1,
                    "未成功发布ACK的同操作恢复一次ACK，不生成新消费或回执");
            }
            using (var f = Fixture(PlainDefinitions(),[],expireTime:checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()+5)))
            {
                var before=f.Read(); var plan=f.Session.PrepareQuestTicket(TicketId,f.Catalog,items.Items)!.Prepared!;
                That(plan.Ticket!.Item.Core.ExpireTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),"实际实例在选角清理后仍有效，正常准备冻结原source");
                await UntilExpired(plan.Ticket.Item.Core.ExpireTime);
                using var output=new ControlledOutput(); using var stop=new CancellationTokenSource();
                await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
                That(await Registry(f).DispatchAsync(Context(f,output,sender,stop),Use(65)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {515}) && output.Frames.Single()[16] == 0,
                    "准备后实际到期的首次515回正常失败ACK，不关闭连接或消费");
                TestCheck.Reject<QuestTicketExpiredException>(()=>f.Repository.CommitQuestQuickClear(f.Owner,plan),"仓储仍按当前时间复验，不沿旧准备时间放行");
                That(Same(before,f.Read()) && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 0,"到期失败保持完成/答案/实际库存/revision");
            }
            using (var f = Fixture(PlainDefinitions(),[]))
            {
                var before=f.Read(); var plan=f.Session.PrepareQuestTicket(TicketId,f.Catalog,items.Items)!.Prepared!;
                var altered=before.Inventory with { Items=before.Inventory.Items.Select(row=>row.Position==Bag(65)
                    ? row with { Core=row.Core with { SortLockFlag=1 } } : row).ToImmutableArray() };
                f.Repository.CommitInventory(f.Owner,before,new(before.Inventory,altered,"ticket_source_test"));
                TestCheck.Reject<InvalidOperationException>(()=>f.Repository.CommitQuestQuickClear(f.Owner,plan),"完整source或revision变化不能消费旧冻结券");
                That(f.Read().Inventory.Find(Bag(65))!.Uid == before.Inventory.Find(Bag(65))!.Uid &&
                    f.Read().CompletedQuests.Length == 0 && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 0,
                    "source CAS失败保留真实UID及全部任务");
            }

            QuestOrdinaryCompletionTests.Fixture Fixture(StoryQuest[] definitions,ActiveMainQuest[] active,bool useCycles=false,
                int expireTime=0,bool firstLocked=false,int? firstExpireTime=null) =>
                new(towns,characters,skills,growth,parameters,items,definitions[0],materialCount:5,definitions:definitions,active:active,
                    configure:candidate=>candidate with
                    {
                        Level=20,InitialLearning=SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,20,
                            candidate.InitialLearning with { Experience=growth.GetLevelStartExperience(20) },skills),
                        // 历史活动券在隔离fixture显式构造期限；生产创建仍Resolve真实STK期限，使用仍检查Core。
                        Inventory=candidate.Inventory with { Items=candidate.Inventory.Items.AddRange(new InventoryItem[] {
                            new(0,Bag(65),InventoryRewardGrantService.Create(items.Items.Get(TicketId)) with
                                { ExpireTime=firstExpireTime ?? expireTime,EquipmentLockId=firstLocked ? (byte)1 : (byte)0 }),
                            new(0,Bag(66),InventoryRewardGrantService.Create(items.Items.Get(TicketId)) with { ExpireTime=expireTime }) }) }
                    },useCycles:useCycles);
            GameCommandRegistry Registry(QuestOrdinaryCompletionTests.Fixture fixture)
            {
                var commands=new GameCommandRegistry(); var projection=new QuestProjection(fixture.Catalog);
                new QuestQuickClearHandler(fixture.Catalog,QuestConnectionCatalog.Parse([],_=>""),projection,log,items.Items).Register(commands);
                new QuestTicketHandler(fixture.Catalog,items.Items,projection,log).Register(commands); commands.Seal(); return commands;
            }
        }
        finally { Directory.Delete(logRoot,true); }
        Console.WriteLine($"PASS {Count-checks} 普通77真实资源、Handler、问题、扣券原子性与发布恢复断言（非主线）");
    }

    static StoryQuest[] PlainDefinitions() => [
        QuestEligibilityTests.Quest(100) with { MinimumLevel=19,Condition="[seeking]",IntData=[10165055,50],
            Rewards=[new(0,10403,null),new(0,1,null)],DeclaredGoldMultiple=50,
            Definition=new([]) { Job="[all]",AcceptanceRewards=[new(0,10165055,null),new(0,3,null)] } },
        QuestEligibilityTests.Quest(101) with { MinimumLevel=20,Definition=new([]) { PrerequisiteGroups=[[100]] } },
        QuestEligibilityTests.Quest(102) with { MinimumLevel=20,Grade="[normal]" },
        QuestEligibilityTests.Quest(103) with { MinimumLevel=21 },
        QuestEligibilityTests.Quest(104) with { Grade="[side]" },
        QuestEligibilityTests.Quest(105) with { Definition=new([]) { JobChange=1 } },
        QuestEligibilityTests.Quest(106) with { Definition=new([]) { PrerequisiteGroups=[[105]] } },
        QuestEligibilityTests.Quest(300) with { Grade="[side]",Condition="[clear quest]",IntData=[101] },
        QuestEligibilityTests.Quest(400) with { MinimumLevel=21,Condition="[seeking]",IntData=[TicketId,2] } ];
    static StoryQuest[] QuestionDefinitions() => [
        QuestEligibilityTests.Quest(100) with { Condition="[question]",Definition=new([new("question info",
            [new(6,0,"标题"),new(6,0,"正文"),new(6,0,"回答一"),new(6,0,"回答二")],[])]) },
        QuestEligibilityTests.Quest(101) with { Definition=new([]) { RequiredAnswers=[new(100,0)] } },
        QuestEligibilityTests.Quest(102) with { Definition=new([]) { RequiredAnswers=[new(100,1)] } } ];
    static ActiveMainQuest Active(byte slot,ushort id,uint scalar=1) => new(slot,id,scalar) { AcceptanceId=Guid.NewGuid() };
    static string Assets(Character character) => JsonSerializer.Serialize(new
        { character.Level,character.Job,character.GrowType,character.EquipmentSlotFlags,character.AcceptedStoryLevel,
            character.Learning,character.Skills,character.Inventory.Gold,character.Inventory.AccountWallet,
            OtherItems=character.Inventory.Items.Where(row=>row.Core.ItemId != TicketId).ToArray(),character.Position,character.Return,character.DungeonState });
    static Packet Use(ushort slot,uint action=77,uint reserved=0,uint first=0,uint second=0,byte list=0)
    {
        byte[] body=new byte[19]; BinaryPrimitives.WriteUInt16LittleEndian(body,slot); body[2]=list;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3),reserved); BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7),action);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(11),first); BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(15),second);
        return new(1,515,body);
    }
    static Packet Prepare() { byte[] body=new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(body,TicketId); return new(1,1430,body); }
    static Packet Answer(uint quest,uint answer)
    {
        byte[] body=new byte[9]; body[0]=1; BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1),quest);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5),answer); return new(1,1431,body);
    }
    static GameConnectionContext Context(QuestOrdinaryCompletionTests.Fixture fixture,Stream output,GameConnectionSender sender,CancellationTokenSource stop) =>
        new(new(90,IPAddress.Loopback,output,stop),fixture.Owner,fixture.Session,sender);
    static long Scalar(QuestOrdinaryCompletionTests.Fixture fixture,string sql)
    { using var db=fixture.Open(); using var command=db.CreateCommand(); command.CommandText=sql; return Convert.ToInt64(command.ExecuteScalar()); }
    static async Task RejectAsync<T>(Func<Task> action,string message) where T:Exception
    { try { await action(); } catch(T) { That(true,message); return; } throw new InvalidOperationException("FAIL: expected "+typeof(T).Name+": "+message); }
    static async Task UntilExpired(int deadline)
    {
        long remaining=Math.Max(0,deadline-DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        That(remaining<=5,"实际期限等待仅限隔离fixture已冻结的最多五秒，不使用假生产时钟");
        while (DateTimeOffset.UtcNow.ToUnixTimeSeconds()<deadline) await Task.Delay(25);
    }
    sealed class ControlledOutput : MemoryStream
    {
        int writes;
        public int FailOn { get; init; }
        public List<byte[]> Frames { get; }=[];
        public IEnumerable<ushort> Opcodes => Frames.Select(frame=>BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)
        { if (++writes==FailOn) throw new IOException("任务券提交后发布故障"); Frames.Add(buffer.ToArray()); return ValueTask.CompletedTask; }
    }
}
