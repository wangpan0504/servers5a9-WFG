using System.Collections.Immutable;
using System.Buffers.Binary;
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
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

// 指定券独立专项；合成515请求验证服务器合同，实际客户端130 use-selector仍待现场验收。
internal static class QuestSpecifiedTicketTests
{
    const int TicketId=10006257;
    static InventorySlot Bag(short slot=65) => new(InventoryListType.Main,slot);

    public static void RunCore()
    {
        int checks=Count;
        var definitions=Definitions(); var quests=new QuestCatalog(definitions.ToDictionary(row=>row.Id));
        var item=new InventoryItem(10,Bag(),new(TicketId,ItemKind.Consumable,2));
        var role=new Character(1,0,"Specified",0,new(1,0,1,1),null)
        {
            Level=60,EquipmentSlotFlags=16,Inventory=new([item]),
            ActiveMainQuests=[Active(0,600),Active(1,100),Active(2,300,0),Active(3,400,1)]
        };
        var definition=new QuestTicketDefinition(TicketId,1,int.MaxValue,ushort.MaxValue,new())
            { ActionIndex=130,TargetQuestIds=[600,100] };
        var before=JsonSerializer.Serialize(role);
        That(Create(role,definition,quests,out var plan) && plan!.Interval.Select(row=>row.Id).SequenceEqual([600,100]) &&
            plan.ScopeQuestIds.SequenceEqual([100,600]) && plan.CompletedAfter.SequenceEqual([100,600]) &&
            plan.EquipmentSlotFlagsAfter==17 && plan.RemovedActive.Select(row=>(int)row.QuestId).SequenceEqual([600,100]) &&
            plan.ActiveAfter.Single(row=>row.QuestId==300).ConditionState==1 &&
            plan.ActiveAfter.Single(row=>row.QuestId==400).ConditionState==0 &&
            plan.Ticket!.Inventory.After.Find(Bag())!.Core.Count==1 && JsonSerializer.Serialize(role)==before,
            "130只逐项推进原序目标，扣一张与槽位、保留寻物、父进度一起冻结，不修改输入");
        using (var snapshot=JsonDocument.Parse(plan!.Snapshot))
            That(snapshot.RootElement.GetProperty("Action").GetUInt32()==130 &&
                snapshot.RootElement.GetProperty("Targets").EnumerateArray().Select(row=>row.GetInt32()).SequenceEqual([600,100]) &&
                snapshot.RootElement.GetProperty("EquipmentSlotFlagsAfter").GetByte()==17,
                "130快照单独保留动作、完整原目标顺序与槽位；不改77/手册历史字段合同");
        var reversed=definition with { TargetQuestIds=[100,600] };
        That(!Create(role,reversed,quests,out _) && Create(role with { CompletedQuests=[900] },reversed,quests,out var alternative) &&
            alternative!.Interval.Select(row=>row.Id).SequenceEqual([100,600]),
            "不排序目标或自动补前置；本服OR前置任一组已完成时允许真实原序");
        var answered=role with { CompletedQuests=[600],QuestAnswers=ImmutableDictionary<int,byte>.Empty.Add(600,1) };
        That(Create(answered,definition,quests,out var partial) && partial!.NewCompletedQuestIds.SequenceEqual([100]) &&
            partial.AnswersAfter[600]==1 && partial.CompletedAfter.SequenceEqual([100,600]),
            "已有目标与持久答案保持，只新增尚未完成的显式目标");
        That(!Create(role with { CompletedQuests=[100,600] },definition,quests,out _) &&
            !Create(role with { Level=59 },definition,quests,out _) &&
            !Create(role,definition with { TargetQuestIds=[600,600] },quests,out _) &&
            !Create(role,definition with { TargetQuestIds=[600,999] },quests,out _),
            "全已完成不消费；等级、重复目标、缺失引用拒绝，不造任务完成或清图凭据");
        foreach (byte job in Enumerable.Range(0,16).Select(row=>(byte)row))
            That(Create(role with { Job=job },definition,quests,out _),"all职业目标无固定职业待遇 "+job);
        foreach (int index in new[] {0,1,2})
        {
            var slotQuest=definitions[1] with { Rewards=[new(0,index,null)] };
            var catalog=new QuestCatalog(definitions.Select(row=>row.Id==100 ? slotQuest : row).ToDictionary(row=>row.Id));
            That(Create(role with { EquipmentSlotFlags=0 },definition,catalog,out var slot) &&
                slot!.EquipmentSlotFlagsAfter==(index switch {0=>1,1=>2,_=>16}),"按声明索引解锁合法装备位 "+index);
        }
        foreach (var unsupported in new[] {
            definitions[1] with { Definition=definitions[1].Definition! with { AccountQuest=true } },
            definitions[1] with { Definition=definitions[1].Definition! with { JobChange=1 } },
            definitions[1] with { RewardType="[tp]",Rewards=[] },
            definitions[1] with { Condition="[question]" } })
        {
            var restricted=new QuestCatalog(definitions.Select(row=>row.Id==100 ? unsupported : row).ToDictionary(row=>row.Id));
            That(!Create(role,definition,restricted,out _),"指定目标专用事务未具备时整笔不消费: "+unsupported.RewardType);
        }
        Console.WriteLine($"PASS {Count-checks} 指定130券原序、OR前置、槽位、保留状态及限制规划断言");
    }

    public static async Task RunAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog? sharedQuests=null,SkillLearningCatalog? sharedSkills=null)
    {
        int checks=Count;
        var quests=sharedQuests ?? QuestCatalog.Load(pvf); var skills=sharedSkills ?? SkillLearningCatalog.Load(pvf);
        var growth=CharacterGrowthCatalog.Load(pvf,skills); var parameters=QuestRewardParameterCatalog.Load(pvf);
        foreach (int template in new[] {10005391,10005390,490000716,10006257})
        {
            var definition=items.Items.QuestTicket(template);
            int[] targets=template==10005390 ? [675,650] : [674,649];
            byte level=template==10005390 ? (byte)65 : (byte)60;
            var real=targets.Select(quests.Get).ToArray();
            That(definition.ActionIndex==130 && definition.TargetQuestIds.SequenceEqual(targets) &&
                definition.MinimumLevel==1 && definition.MaximumLevel==int.MaxValue,
                "真实指定券保留原序目标、min和缺省max: "+template);
            using var f=Fixture(template,real,level,0,[Active(0,(ushort)targets[0]),Active(1,(ushort)targets[1])]);
            var before=f.Read(); var source=before.Inventory.Find(Bag())!;
            var commit=f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,130)!.Commit!; var after=f.Read();
            byte flags=(byte)(before.EquipmentSlotFlags | (template==10005390 ? 2 : 1));
            That(!commit.Duplicate && commit.Plan.Interval.Select(row=>row.Id).SequenceEqual(targets) &&
                after.CompletedQuests.SequenceEqual(targets.Order()) && after.ActiveMainQuests.Length==0 &&
                after.EquipmentSlotFlags==flags && after.Revision==before.Revision+1 &&
                after.Inventory.Find(Bag())!.Uid==source.Uid && after.Inventory.Find(Bag())!.Core==(source.Core with { Value=source.Core.Value-1 }) &&
                Assets(before,template)==Assets(after,template),
                "真实目标只扣一券并保存槽位/完成，不发普通奖励、金币或扣寻物材料: "+template);
            That(Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts WHERE operation_kind='Ticket'")==1 &&
                f.Repository.CommitQuestQuickClear(f.Owner,commit.Plan) is { Duplicate:true,ProjectionCurrent:true } &&
                f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,130)?.Commit is { Duplicate:true } && Same(after,f.Read()),
                "真实130沿同一Operation/source/快照恢复，不重扣、不增加revision: "+template);
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(after,f.Session.Selected!),"重选回放指定券后的槽位、实际库存和完成记录: "+template);
        }
        var actual=items.Items.QuestTicket(TicketId); var targetDefinitions=actual.TargetQuestIds.Select(quests.Get).ToArray();
        using (var f=Fixture(TicketId,targetDefinitions,60,15,[]))
        {
            var before=f.Read();
            That(f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,130)?.Commit is not null && f.Read().Job==15 &&
                f.Read().EquipmentSlotFlags==17 && Assets(before,TicketId)==Assets(f.Read(),TicketId),
                "真实all指定券覆盖枪剑士，槽位事务保留该职业真实技能及成长");
        }
        using (var f=Fixture(TicketId,targetDefinitions,59,0,[]))
        {
            var before=f.Read();
            That(f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,130) is { Commit:null } && Same(before,f.Read()) &&
                Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts")==0,
                "券自身min1不绕过目标Lv60资格；失败不消费");
        }
        using (var f=Fixture(TicketId,targetDefinitions,60,0,[],completed:actual.TargetQuestIds.ToArray()))
        {
            var before=f.Read();
            That(f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,130) is { Commit:null } && Same(before,f.Read()),
                "所有目标已有完成记录时不扣券或补发槽位");
        }
        var synthetic=Definitions();
        using (var f=Fixture(TicketId,synthetic,60,0,[Active(0,600),Active(1,100),Active(2,300,0),Active(3,400,1)]))
        {
            var before=f.Read();
            // 本fixture的资源template仍指向真实674/649；合成领域直接冻结所需targets后验证同一仓储合同。
            var source=QuestTicketSource.Create(before,Bag(),new(TicketId,1,int.MaxValue,ushort.MaxValue,new())
                { ActionIndex=130,TargetQuestIds=[600,100] },DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            That(QuestQuickClearPlan.TryCreateTicket(before,Guid.NewGuid(),f.Catalog,source,ImmutableDictionary<int,byte>.Empty,out var plan,out _),
                "合成保留寻物/父状态规划复用真实券实例");
            f.Sql("CREATE TRIGGER specified_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'specified rollback'); END");
            TestCheck.Reject<SqliteException>(()=>f.Repository.CommitQuestQuickClear(f.Owner,plan!),"指定券回执末段SQL故障");
            That(Same(before,f.Read()) && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts")==0,
                "券、槽flag、活动槽、保留进度、完成记录与revision整笔回滚");
            f.Sql("DROP TRIGGER specified_abort");
            var committed=f.Repository.CommitQuestQuickClear(f.Owner,plan!); var after=f.Read();
            That(after.ActiveMainQuests.Single(row=>row.QuestId==300).ConditionState==1 &&
                after.ActiveMainQuests.Single(row=>row.QuestId==400).ConditionState==0 && after.EquipmentSlotFlags==17 &&
                after.Revision==before.Revision+1 && Assets(before,TicketId)==Assets(after,TicketId) &&
                f.Repository.CommitQuestQuickClear(f.Owner,plan!) is { Duplicate:true,ProjectionCurrent:true } && Same(after,committed.Character),
                "真实库存扣减后寻物与父状态同时保存，提交返回丢失沿冻结回执恢复");
        }
        string logRoot=Path.Combine(Path.GetTempPath(),"S5A9-specified-ticket-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logRoot);
        try
        {
            using var log=new ServerLog(logRoot);
            using (var f=Fixture(TicketId,targetDefinitions,60,15,[Active(0,674),Active(1,649)]))
            {
                var before=f.Read(); using var output=new ControlledOutput(); using var stop=new CancellationTokenSource();
                await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
                var context=Context(f,output,sender,stop); var commands=Registry(f,log);
                foreach (var invalid in new[] { Use(action:77),Use(action:119),Use(first:1),Use(reserved:1) })
                {
                    That(await commands.DispatchAsync(context,invalid)==CommandResult.Handled &&
                        output.Opcodes.SequenceEqual(new ushort[] {515}) && output.Frames.Single()[16]==0 && Same(before,f.Read()),
                        "515动作与实际指定券声明、已确认零参数严格匹配；失败不提交");
                    output.Frames.Clear();
                }
                That(await commands.DispatchAsync(context,Use())==CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {515,2,574,21,356}),
                    "指定券真实Handler按ACK515→Player槽位绝对快照→活动/可接/完成发布");
                Bytes(output.Frames[0].AsSpan(16),[1,65,0,0,130,0,0,0],"130成功ACK8B，无数量/附加奖励或NOTI14");
                var after=f.Read();
                Bytes(output.Frames[1].AsSpan(16),TownPackets.Player(after,new CharacterStatComputer(characters).Compute(after)),
                    "Player正文使用已提交的全部角色状态，不只拼槽位字段");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Use())==CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {2,574,21,356}) && Same(after,f.Read()) &&
                    after.Revision==before.Revision+1 && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts")==1,
                    "已发布消费ACK的重复515仅回放绝对状态，不再次扣券或客户端减一");
            }
            using (var f=Fixture(TicketId,targetDefinitions,60,0,[]))
            {
                var before=f.Read(); var commands=Registry(f,log);
                using (var broken=new ControlledOutput { FailOn=1 })
                using (var stop=new CancellationTokenSource())
                await using (var sender=new GameConnectionSender(f.Owner,broken,stop,0,(_,_,_,_,_)=>{}))
                    await RejectAsync<IOException>(()=>commands.DispatchAsync(Context(f,broken,sender,stop),Use()));
                var committed=f.Read(); using var output=new ControlledOutput(); using var retryStop=new CancellationTokenSource();
                await using var retrySender=new GameConnectionSender(f.Owner,output,retryStop,0,(_,_,_,_,_)=>{});
                That(await commands.DispatchAsync(Context(f,output,retrySender,retryStop),Use())==CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {515,2,574,21,356}) && Same(committed,f.Read()) &&
                    committed.Revision==before.Revision+1 && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts")==1,
                    "提交后ACK发送失败恢复同一操作，券/槽位/完成不重复提交");
            }
            using (var f=Fixture(TicketId,targetDefinitions,60,0,[Active(0,674),Active(1,649)]))
            {
                var before=f.Read(); var prepared=f.Session.PrepareQuestTicket(TicketId,f.Catalog,items.Items)!.Prepared!;
                f.Sql("CREATE TRIGGER specified_session_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'specified rollback'); END");
                TestCheck.Reject<SqliteException>(()=>f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,130),"真实指定券Session回执SQL失败");
                That(Same(before,f.Read()),"真实目标和slotflags与券一次回滚");
                var bound=f.Session.PrepareQuestTicket(TicketId,f.Catalog,items.Items)!.Prepared!;
                That(bound.OperationId==prepared.OperationId && bound.Ticket!.Item.Position==Bag(),"SQL失败保留首次515最终绑定Operation/source");
                f.Sql("DROP TRIGGER specified_session_abort");
                var saved=f.Repository.CommitQuestQuickClear(f.Owner,bound);
                That(f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,130)?.Commit is { Duplicate:true,ProjectionCurrent:true } &&
                    Same(saved.Character,f.Read()) && f.Read().Revision==before.Revision+1,
                    "模拟提交成功返回丢失，Session沿同一冻结源与槽位结果恢复");
            }
        }
        finally { Directory.Delete(logRoot,true); }
        Console.WriteLine($"PASS {Count-checks} 指定130券资源、资格、槽位事务、Handler及恢复断言（非主线）");

        QuestOrdinaryCompletionTests.Fixture Fixture(int template,StoryQuest[] definitions,byte level,byte job,ActiveMainQuest[] active,
            int[]? completed=null) =>
            new(towns,characters,skills,growth,parameters,items,definitions[0],materialCount:5,definitions:definitions,active:active,
                configure:candidate=>
                {
                    var created=CharacterCreation.Prepare(candidate.Name,characters.Get(job),towns.Room,towns.InitialReturn,skills.Get);
                    return created with { Level=level,EquipmentSlotFlags=16,CompletedQuests=completed ?? [],
                        InitialLearning=SkillPointLedger.Synchronize(job,created.GrowType,level,
                            created.InitialLearning with { Experience=growth.GetLevelStartExperience(level) },skills),
                        Inventory=created.Inventory with { Gold=900000,Items=created.Inventory.Items.Add(new(0,Bag(),
                            InventoryRewardGrantService.Create(items.Items.Get(template),2) with { ExpireTime=0 })) } };
                });
        GameCommandRegistry Registry(QuestOrdinaryCompletionTests.Fixture f,ServerLog log)
        {
            var registry=new GameCommandRegistry(); var projection=new QuestProjection(f.Catalog);
            new QuestTicketHandler(f.Catalog,items.Items,projection,log,
                new CharacterLevelProjection(new CharacterStatComputer(characters),projection)).Register(registry);
            registry.Seal(); return registry;
        }
    }

    static bool Create(Character expected,QuestTicketDefinition definition,QuestCatalog quests,out QuestQuickClearPlan? plan) =>
        QuestQuickClearPlan.TryCreateTicket(expected,Guid.NewGuid(),quests,
            QuestTicketSource.Create(expected,Bag(),definition,100),ImmutableDictionary<int,byte>.Empty,out plan,out _);
    static StoryQuest[] Definitions() => [
        QuestEligibilityTests.Quest(600) with { MinimumLevel=60,Condition="[hunt monster]",IntData=[3,0,1,1],
            Rewards=[new(0,10403,null),new(0,1,null)],DeclaredGoldMultiple=100 },
        QuestEligibilityTests.Quest(100) with { Grade="[side]",MinimumLevel=60,Condition="[seeking]",IntData=[0,150000,10165055,50],
            RewardType="[slot expansion]",Rewards=[new(0,0,null)],DeclaredGoldMultiple=100,
            Definition=new([]) { Job="[all]",PrerequisiteGroups=[[600],[900]],AcceptanceRewards=[new(0,10165055,null),new(0,3,null)] } },
        QuestEligibilityTests.Quest(300) with { MinimumLevel=90,Condition="[seeking]",IntData=[TicketId,2] },
        QuestEligibilityTests.Quest(400) with { Grade="[side]",Condition="[clear quest]",IntData=[100] },
        QuestEligibilityTests.Quest(900) ];
    static ActiveMainQuest Active(byte slot,ushort id,uint state=1) => new(slot,id,state) { AcceptanceId=Guid.NewGuid() };
    static string Assets(Character role,int ticket) => JsonSerializer.Serialize(new { role.Level,role.Job,role.GrowType,role.AcceptedStoryLevel,
        role.Learning,role.Skills,role.Inventory.Gold,role.Inventory.AccountWallet,
        OtherItems=role.Inventory.Items.Where(row=>row.Core.ItemId!=ticket).ToArray(),role.Position,role.Return,role.DungeonState });
    static long Scalar(QuestOrdinaryCompletionTests.Fixture f,string sql)
    { using var db=f.Open(); using var c=db.CreateCommand(); c.CommandText=sql; return Convert.ToInt64(c.ExecuteScalar()); }
    static Packet Use(uint action=130,uint reserved=0,uint first=0)
    {
        byte[] body=new byte[19]; BinaryPrimitives.WriteUInt16LittleEndian(body,65);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3),reserved); BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7),action);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(11),first); return new(1,515,body);
    }
    static GameConnectionContext Context(QuestOrdinaryCompletionTests.Fixture f,Stream output,GameConnectionSender sender,CancellationTokenSource stop) =>
        new(new(91,IPAddress.Loopback,output,stop),f.Owner,f.Session,sender);
    static async Task RejectAsync<T>(Func<Task> action) where T:Exception
    { try { await action(); } catch(T) { That(true,"指定券提交后的ACK发布失败"); return; } throw new InvalidOperationException("FAIL expected "+typeof(T).Name); }
    sealed class ControlledOutput : MemoryStream
    {
        int writes; public int FailOn { get; init; } public List<byte[]> Frames { get; }=[];
        public IEnumerable<ushort> Opcodes => Frames.Select(row=>BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(1)));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)
        { if (++writes==FailOn) throw new IOException("指定券提交后发布故障"); Frames.Add(buffer.ToArray()); return ValueTask.CompletedTask; }
    }
}
