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
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

// 独立券机制；不推进主线或把合成515 PASS视为客户端验收。
internal static class QuestAwakeningTicketTests
{
    const int FirstTicket=10162928,SecondTicket=10162929;
    static InventorySlot Bag() => new(InventoryListType.Main,65);

    public static void RunCore()
    {
        int checks=Count;
        var template=new ItemTemplate(FirstTicket,ItemKind.Consumable,0,50,0,1,0,0,50,"",ushort.MaxValue,0,"Awaken");
        var raw=new StackableFile(FirstTicket,"synthetic.stk",new("[consumable]",[0]),1,0,0,50,null,[])
        { ActionDeclaration=new("[first awaken clear]",[1]),HasUseActionPacket=true,DeclaredUseActionPacket=1,
            HasActionUsablePlace=true,DeclaredActionUsablePlaces=["[village]"] };
        var first=QuestTicketDefinition.Parse(raw,template);
        var second=QuestTicketDefinition.Parse(raw with { TemplateId=SecondTicket,DeclaredMinimumLevel=75,
            ActionDeclaration=new("[second awaken clear]",[0]) },template with { Id=SecondTicket,MinimumLevel=75 });
        That(first is {ActionIndex:119,AwakeningStage:1,MinimumLevel:50,VillageOnly:true} && second is {ActionIndex:122,AwakeningStage:2,MinimumLevel:75,VillageOnly:true},
            "匹配非空[1]/[0]觉醒券参数、stage与真实tag1保留");
        foreach(var invalid in new[] { raw with { DeclaredUseActionPacket=0 },raw with { HasUseActionPacket=false },
            raw with { ActionDeclaration=new("[first awaken clear]",[]) },raw with { ActionDeclaration=new("[first awaken clear]",[1,2]) },
            raw with { ActionDeclaration=new("[second awaken clear]",[1]) } })
            DfoServer.Testing.TestCheck.Reject<NotSupportedException>(()=>QuestTicketDefinition.Parse(invalid,template),
                "不猜造空参数旧券或不同use-action-packet形状");
        That(QuestTicketDefinition.Parse(raw with { ActionDeclaration = new("[first awaken clear]", [1,1]) },template).AwakeningStage == 1 &&
            QuestTicketDefinition.Parse(raw with { ActionDeclaration = new("[second awaken clear]", [0,1]) },template).AwakeningStage == 2,
            "含自我觉醒标志的双参数券沿现有职业任务、CHR赠技与原子提交，不凭标志直接改职业");
        var mixed = QuestTicketDefinition.Parse(raw with { DeclaredActionUsablePlaces=["[village]","[seria room]","[village]"] },template);
        That(mixed.Places.SequenceEqual(new[] { "[village]", "[seria room]", "[village]" }) &&
            StackableUsePlaces.Allows(mixed.Places,"[village]") && StackableUsePlaces.Allows(mixed.Places,"[seria room]") &&
            !StackableUsePlaces.Allows(mixed.Places,"[dungeon]"),"觉醒券保留多地点及重复声明，按实际地点匹配");
        That(QuestTicketDefinition.Parse(raw with { DeclaredActionUsablePlaces=[] },template).Places.IsEmpty,
            "空地点列表不因数量拒绝觉醒券定义");
        foreach(byte job in Enumerable.Range(0,16).Select(value=>(byte)value))
            That(first.Allows(50,job) && second.Allows(75,job) && !first.Allows(49,job) && !second.Allows(74,job),
                "真实all职业及50/75边界 "+job);
        Console.WriteLine($"PASS {Count-checks} 觉醒券资源形状与资格核心断言");
    }

    public static async Task RunAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog? sharedQuests=null,SkillLearningCatalog? sharedSkills=null)
    {
        int checks=Count;
        var quests=sharedQuests ?? global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf); var skills=sharedSkills ?? global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth=global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills); var parameters=global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var first=items.Items.QuestTicket(FirstTicket); var second=items.Items.QuestTicket(SecondTicket);
        That(first is {ActionIndex:119,AwakeningStage:1,MinimumLevel:50,VillageOnly:true} && second is {ActionIndex:122,AwakeningStage:2,MinimumLevel:75,VillageOnly:true},
            "两张匹配PVF非空参数有效券装载");
        foreach (var (id, action) in new[] { (490003900, 119u), (490003901, 122u) })
        {
            var actual = items.Items.QuestTicket(id);
            That(actual.ActionIndex == action && actual.Places.Contains("[village]") && actual.Places.Contains("[seria room]") &&
                StackableUsePlaces.Allows(actual.Places, "[village]"), "真实双地点觉醒券可按城镇资格装载 " + id);
        }
        var jobs=new HashSet<byte>(); var unavailable=new List<string>(); int combinations=0;
        foreach(byte job in Enumerable.Range(0,16).Select(value=>(byte)value))
        foreach(byte branch in (job is 9 or 10 ? new byte[] {0} : new byte[] {1,2,3,4,5}))
        foreach(byte stage in new byte[] {1,2})
        {
            byte beforeGrow=checked((byte)(branch | (stage-1)<<4));
            try { _=skills.FreeSkills(job,beforeGrow); _=skills.FreeSkills(job,checked((byte)(branch | stage<<4))); }
            catch(NotSupportedException) {continue;} // CHR不存在的组合不伪造分支。
            var role=Role(job,beforeGrow,stage,stage==1 ? first : second) with { CompletedQuests=[3356,3427] };
            // CHR也有保留空块/占位名；只有实际QST职业终点授权此分支。探针仅隔离角色资格，真实规划仍核完整前置。
            bool endpoint=quests.All.Any(quest=>MatchingEndpoint(role,quest,stage));
            if (!endpoint)
            {
                That(!Create(role,stage==1 ? first : second,quests,out _),$"CHR索引无有效QST觉醒终点时明确拒绝 {job}/{branch}/{stage}");
                unavailable.Add($"{job}/{branch}/{stage}"); continue;
            }
            That(Create(role,stage==1 ? first : second,quests,out var plan) &&
                plan!.ProfessionGrowType==(branch | stage<<4) && plan.Interval.All(row=>row.Definition!.JobChange==stage+1) &&
                plan.Interval.Any(row=>row.RewardType=="[awakening type]") && plan.TargetLearning.Experience==role.Learning.Experience &&
                plan.TargetLearning.BonusSp==137 && plan.TargetLearning.BonusTp==11 &&
                plan.CompletedAfter.Contains(3356) && plan.CompletedAfter.Contains(3427) &&
                plan.Ticket!.Inventory.After.Find(Bag()) is null && plan.Ticket.Inventory.After.Find(new(InventoryListType.Main,66))!.Core.Count==1,
                $"真实资源仅完成本job/grow可达stage+1链，普通剧情前置保持 {job}/{branch}/{stage}");
            jobs.Add(job); combinations++;
        }
        That(jobs.Count==16 && combinations>=100,"16职业各自有实际QST终点且CHR可读的合法组合逐级觉醒");
        Console.WriteLine("觉醒券资源限制（job/branch/stage，无有效QST终点）: "+string.Join(", ",unavailable));
        var realRole=Role(0,1,1,first);
        That(!Create(realRole with {Level=49},first,quests,out _) &&
            !Create(realRole with {GrowType=17},first,quests,out _) &&
            !Create(realRole,second,quests,out _) &&
            !Create(realRole with {Learning=realRole.Learning with {BonusTp=null}},first,quests,out _),
            "等级不足、已一觉再一觉、未一觉直接二觉、未知点数来源均不消费");

        foreach(byte stage in new byte[] {1,2})
        {
            var ticket=stage==1 ? first : second;
            var definitions=Definitions(stage);
            var catalog=new QuestCatalog(definitions.ToDictionary(row=>row.Id));
            var role=Role(0,checked((byte)(1 | (stage-1)<<4)),stage,ticket) with
                { CompletedQuests=[900],ActiveMainQuests=[Active(0,100),Active(1,101),Active(2,300,0),Active(3,400,1)] };
            That(Create(role,ticket,catalog,out var plan) && plan!.Interval.Select(row=>row.Id).SequenceEqual([100,101]) &&
                plan.RemovedActive.Select(row=>(int)row.QuestId).SequenceEqual([100,101]) &&
                plan.ActiveAfter.Single(row=>row.QuestId==300).ConditionState==1 &&
                plan.ActiveAfter.Single(row=>row.QuestId==400).ConditionState==0 &&
                plan.AnswersAfter.Count==role.QuestAnswers.Count && PreservesLearning(role.Learning,plan.TargetLearning),
                "OR前置只需真实一组，扣券刷新剩余寻物/父状态；普通奖励/赠品/材料不执行");
            using(var snapshot=JsonDocument.Parse(plan!.Snapshot))
                That(snapshot.RootElement.GetProperty("Action").GetUInt32()==ticket.ActionIndex &&
                    snapshot.RootElement.GetProperty("ProfessionGrowType").GetByte()==(1 | stage<<4),
                    "仅觉醒类型Snapshot追加动作、完整职业与两树结果");
            var ordinary=new QuestCatalog(new[] {definitions[0] with {Definition=definitions[0].Definition! with {JobChange=0}},
                definitions[1],definitions[4]}.ToDictionary(row=>row.Id));
            That(!Create(role,ticket,ordinary,out _) && !Create(role with {CompletedQuests=[100,101,900]},ticket,catalog,out _),
                "无可达真实觉醒终点/全已完成均拒绝，不补普通前置、不重复消费");
            var unanswered=definitions[0] with {Condition="[question]",IntData=[2],Definition=definitions[0].Definition! with
                {Fields=[new("question info",[new(6,0,"question"),new(6,0,"prompt"),new(6,0,"yes"),new(6,0,"no")],[])]}};
            var questions=new QuestCatalog(definitions.Select(row=>row.Id switch {100=>unanswered,
                101=>row with {Definition=row.Definition! with {RequiredAnswers=[new(100,0)]}},_=>row}).ToDictionary(row=>row.Id));
            That(!Create(role with {ActiveMainQuests=[Active(0,100,QuestQuestionProgress.Unanswered)]},ticket,questions,out _) &&
                Create(role with {ActiveMainQuests=[Active(0,100,0)]},ticket,questions,out var answered) && answered!.AnswersAfter[100]==0,
                "未回答不能猜造；真实已接问题的零基选择在券同事务保存");
            using(var f=Make(stage,definitions))
            {
                var before=f.Read(); var prepared=Plan(before,ticket,f.Catalog);
                f.Sql("CREATE TRIGGER awaken_ticket_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'awakening ticket rollback'); END");
                DfoServer.Testing.TestCheck.Reject<SqliteException>(()=>f.Repository.CommitQuestQuickClear(f.Owner,prepared),"觉醒券末段SQL故障");
                That(Same(before,f.Read()) && Receipts(f)==0,"职业、两树、规范技能、UID券、完成和活动槽全部回滚");
                f.Sql("DROP TRIGGER awaken_ticket_abort");
                var saved=f.Repository.CommitQuestQuickClear(f.Owner,prepared); var after=f.Read();
                That(saved is {Duplicate:false,ProjectionCurrent:true} && after.Revision==before.Revision+1 &&
                    after.GrowType==(1 | stage<<4) && after.Level==before.Level && after.Inventory.Gold==before.Inventory.Gold &&
                    after.Inventory.Find(Bag()) is null &&
                    after.Inventory.Find(new(InventoryListType.Main,66))!.Uid==before.Inventory.Find(new(InventoryListType.Main,66))!.Uid &&
                    PreservesLearning(before.Learning,after.Learning) &&
                    after.Skills.SequenceEqual(after.Learning.Trees[0].Skills.Select(row=>new CharacterSkill(row.Id,row.Level)).OrderBy(row=>row.Id)) &&
                    after.CompletedQuests.SequenceEqual([100,101,900]) && Receipts(f)==1,
                    "消费券、逐级职业、两树来源与规范技能一次revision；经验金币/普通奖励保持");
                That(f.Repository.CommitQuestQuickClear(f.Owner,prepared) is {Duplicate:true,ProjectionCurrent:true} && Same(after,f.Read()),
                    "模拟已提交返回丢失沿原Operation/source/grow/learning精确回执恢复");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(Same(after,f.Session.Selected!),"重选恢复同职业、规范技能和完整两树，旧Operation不重新消费");
            }
        }

        string logRoot=Path.Combine(Path.GetTempPath(),"S5A9-awakening-ticket-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logRoot);
        try
        {
            using var log=new ServerLog(logRoot);
            var secondSteps=Definitions(2).Take(2).Select(row=>row with
            {
                Id=row.Id+10,
                Definition=row.Definition! with {PrerequisiteGroups=[[row.Id==100 ? 101 : 110]]}
            });
            using (var f=Make(1,Definitions(1).Concat(secondSteps).ToArray(),consecutive:true))
            {
                var firstUse=f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,119,skills)!;
                That(firstUse.Commit is {Duplicate:false} && firstUse.Character.GrowType==17,
                    "连续觉醒先提交一觉职业与消费");
                var firstSaved=f.Read();
                That(f.Session.UseQuestTicket(66,0,f.Catalog,items.Items,122,skills) is {Commit:null} &&
                    f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,119,skills)?.Commit is {Duplicate:true} && Same(firstSaved,f.Read()),
                    "消费ACK尚未确认时不切换到二觉券，保留一觉原提交恢复");
                f.Session.MarkQuestTicketAcknowledged(firstUse);
                using var output=new ControlledOutput(); using var stop=new CancellationTokenSource();
                await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
                var commands=Registry(f,log); var context=Context(f,output,sender,stop);
                That(await commands.DispatchAsync(context,Use(122,66))==CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {19,515,2,2,574,21,356}) && output.Frames[1][16]==1,
                    "一觉ACK确认后不重选角可直接完成二觉及其完整投影");
                var secondSaved=f.Read(); output.Frames.Clear();
                That(secondSaved.GrowType==33 && secondSaved.Revision==firstSaved.Revision+1 &&
                    secondSaved.CompletedQuests.SequenceEqual(new[] {100,101,110,111,900}) &&
                    secondSaved.Inventory.Find(Bag()) is null && secondSaved.Inventory.Find(new(InventoryListType.Main,66)) is null &&
                    PreservesLearning(firstSaved.Learning,secondSaved.Learning) && Receipts(f)==2,
                    "连续觉醒分别消费和持久化，保留一觉技能与完成记录");
                That(await commands.DispatchAsync(context,Use(122,66))==CommandResult.Handled &&
                    !output.Opcodes.Contains((ushort)515) && Same(secondSaved,f.Read()),
                    "二觉已确认的重复请求仍不重发消费ACK或新建事务");
            }
            foreach(byte stage in new byte[] {1,2})
            using(var f=Make(stage,Definitions(stage)))
            {
                var before=f.Read(); using var output=new ControlledOutput(); using var stop=new CancellationTokenSource();
                await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
                var context=Context(f,output,sender,stop); var commands=Registry(f,log); uint action=stage==1 ? 119u : 122u;
                That(await commands.DispatchAsync(context,Use(action))==CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {19,515,2,2,574,21,356}),
                    "真实Handler先19技能快照→515消费→Actor/Player职业→574/21/356 "+stage);
                var after=f.Read();
                Bytes(output.Frames[0].AsSpan(16),SkillPackets.Initialized(after.Learning),"19来源与两树余额来自原子提交");
                Bytes(output.Frames[1].AsSpan(16),[1,65,0,0,(byte)action,0,0,0],"ACK5158B，客户端自行减1，无14/34普通奖励");
                Bytes(output.Frames[2].AsSpan(16),TownPackets.Actor(after, 0),"Actor携带真实packed grow");
                Bytes(output.Frames[3].AsSpan(16),TownPackets.Player(after,new CharacterStatComputer(characters).Compute(after),1),"Player使用目标职业CHR属性");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Use(action))==CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {19,2,2,574,21,356}) && Same(after,f.Read()) && Receipts(f)==1 &&
                    after.Revision==before.Revision+1,"重复515只回放绝对职业状态，不再消费或发布消费ACK");
                foreach(ushort id in new ushort[] {100,101,100})
                {
                    output.Frames.Clear();
                    That(await commands.DispatchAsync(context,Giveup(id))==CommandResult.Handled &&
                        output.Opcodes.SequenceEqual(new ushort[] {32}) && output.Frames[0][16]==1 && Same(after,f.Read()),
                        "本地helper随后32仅确认本回执真实RemovedActive槽，不回收资产 "+id);
                }
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Giveup(900))==CommandResult.Handled && output.Frames[0][16]==0 && Same(after,f.Read()),
                    "不泛化确认未在本次关闭槽内的已完成任务");
                That(f.Change(800,QuestLifecycleAction.Accept)?.Commit is not null,"独立合法任务更新实际Selected/revision");
                var changed=f.Read(); output.Frames.Clear();
                That(await commands.DispatchAsync(context,Giveup(100))==CommandResult.Handled && output.Frames[0][16]==0 && Same(changed,f.Read()),
                    "后续真实源状态变化不沿旧券32确认上下文");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady(); output.Frames.Clear();
                That(await commands.DispatchAsync(context,Giveup(100))==CommandResult.Handled && output.Frames[0][16]==0 && Same(changed,f.Read()),
                    "重选隔离旧券32恢复上下文");
            }
            using(var f=Make(1,Definitions(1)))
            {
                var before=f.Read(); var pending=f.Session.PrepareQuestTicket(FirstTicket,f.Catalog,items.Items,skills)!.Prepared!;
                f.Sql("CREATE TRIGGER awaken_ticket_session_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'awakening ticket rollback'); END");
                DfoServer.Testing.TestCheck.Reject<SqliteException>(()=>f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,119,skills),"觉醒券Session未知SQL失败");
                That(Same(before,f.Read()) && ReferenceEquals(f.Session.Selected!.Learning,pending.Expected.Learning),"失败不安装职业或技能候选");
                f.Sql("DROP TRIGGER awaken_ticket_session_abort");
                var bound=f.Session.PrepareQuestTicket(FirstTicket,f.Catalog,items.Items,skills)!.Prepared!;
                That(bound.OperationId==pending.OperationId,"失败保留同一最终绑定源和Operation");
                var saved=f.Repository.CommitQuestQuickClear(f.Owner,bound);
                That(f.Session.UseQuestTicket(65,0,f.Catalog,items.Items,119,skills)?.Commit is {Duplicate:true,ProjectionCurrent:true} &&
                    Same(saved.Character,f.Read()) && Receipts(f)==1,"Session返回丢失恢复同一提交职业/券，不重规划或扣第二张");
            }
            using(var f=Make(2,Definitions(2)))
            {
                var before=f.Read(); var commands=Registry(f,log);
                using(var broken=new ControlledOutput {FailOn=2}) using(var stop=new CancellationTokenSource())
                await using(var sender=new GameConnectionSender(f.Owner,broken,stop,0,(_,_,_,_,_)=>{}))
                    await RejectAsync<IOException>(()=>commands.DispatchAsync(Context(f,broken,sender,stop),Use(122)));
                var saved=f.Read(); using var output=new ControlledOutput(); using var retryStop=new CancellationTokenSource();
                await using var retrySender=new GameConnectionSender(f.Owner,output,retryStop,0,(_,_,_,_,_)=>{});
                That(await commands.DispatchAsync(Context(f,output,retrySender,retryStop),Use(122))==CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {19,515,2,2,574,21,356}) && Same(saved,f.Read()) &&
                    saved.Revision==before.Revision+1 && Receipts(f)==1,"19已发后ACK丢失重试同提交，不重复职业或消费");
            }
        }
        finally {TestFiles.DeleteDirectory(logRoot,true);}
        Console.WriteLine($"PASS {Count-checks} 觉醒119/122真实资源、逐级资格、职业学习事务及发布恢复断言（非主线）");

        Character Role(byte job,byte grow,byte stage,QuestTicketDefinition ticket)
        {
            byte level=stage==1 ? (byte)50 : (byte)75;
            var created=CharacterCreation.Prepare("Awaken",characters.Get(job),towns.Room,towns.InitialReturn,skills.Get);
            var learning=SkillPointLedger.Synchronize(job,grow,level,created.InitialLearning with
                {Experience=growth.GetLevelStartExperience(level),BonusSp=137,BonusTp=11},skills);
            return new(1,0,"Awaken",0,created.Position,created.Return) {Job=job,GrowType=grow,Level=level,Learning=learning,
                Skills=learning.Trees[0].Skills.Select(row=>new CharacterSkill(row.Id,row.Level)).OrderBy(row=>row.Id).ToArray(),
                Inventory=created.Inventory with {Gold=900000,Items=created.Inventory.Items.AddRange(new[] {
                    new InventoryItem(10,Bag(),InventoryRewardGrantService.Create(items.Items.Get(ticket.TemplateId)) with {ExpireTime=0}),
                    new InventoryItem(11,new(InventoryListType.Main,66),InventoryRewardGrantService.Create(items.Items.Get(ticket.TemplateId)) with {ExpireTime=0})})}};
        }
        bool Create(Character role,QuestTicketDefinition ticket,QuestCatalog catalog,out QuestQuickClearPlan? plan)
        {
            try {return QuestQuickClearPlan.TryCreateTicket(role,Guid.NewGuid(),catalog,
                QuestTicketSource.Create(role,Bag(),ticket,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),ImmutableDictionary<int,byte>.Empty,out plan,out _,skills);}
            catch(InvalidDataException) {plan=null;return false;}
        }
        bool MatchingEndpoint(Character role,StoryQuest quest,byte stage)
        {
            if (quest.RewardType!="[awakening type]" || quest.Definition is not {AccountQuest:false} definition ||
                definition.JobChange!=stage+1 || !QuestCoverage.HasOrdinaryDeclarations(quest)) return false;
            var probe=quest with {Definition=definition with {PrerequisiteGroups=[]}};
            if (!QuestEligibility.Evaluate(role,probe,quests).Eligible) return false;
            try {return QuestProfessionReward.Target(role,quest,skills)==(role.GrowType&15 | stage<<4);}
            catch(Exception error) when(error is InvalidDataException or NotSupportedException or KeyNotFoundException) {return false;}
        }
        QuestQuickClearPlan Plan(Character role,QuestTicketDefinition ticket,QuestCatalog catalog)
        {That(Create(role,ticket,catalog,out var plan),"合法觉醒券事务fixture规划");return plan!;}
        QuestOrdinaryCompletionTests.Fixture Make(byte stage,StoryQuest[] definitions,bool consecutive=false) =>
            new(towns,characters,skills,growth,parameters,items,definitions[0],materialCount:0,definitions:definitions,
                active:[Active(0,100),Active(1,101),Active(2,300,0),Active(3,400,1)],configure:candidate=>
                {
                    var role=Role(0,checked((byte)(1 | (stage-1)<<4)),stage,stage==1 ? first : second);
                    if (consecutive)
                        role=role with {Level=75,Learning=SkillPointLedger.Synchronize(role.Job,role.GrowType,75,
                            role.Learning with {Experience=growth.GetLevelStartExperience(75)},skills),
                            Inventory=role.Inventory with {Items=role.Inventory.Items.Select(row=>row.Position.Index==66
                                ? row with {Core=InventoryRewardGrantService.Create(items.Items.Get(SecondTicket)) with {ExpireTime=0}}
                                : row).ToImmutableArray()}};
                    var paid=SkillLearning.Plan(0,role.GrowType,role.Level,role.Learning,new(0,0,[new(5,false,2)]),skills);
                    That(paid.Success,"觉醒券fixture保留真实付费技能来源");
                    var learning=paid.State! with {Trees=paid.State!.Trees.Select(tree=>tree with
                        {Skills=tree.Skills.Select(row=>row.Id==5 ? row with {Command="000302"} : row).ToArray()}).ToArray()};
                    return candidate with {GrowType=role.GrowType,Level=role.Level,InitialLearning=learning,CompletedQuests=[900],
                        Skills=learning.Trees[0].Skills.Select(row=>new CharacterSkill(row.Id,row.Level)).OrderBy(row=>row.Id).ToArray(),
                        Inventory=role.Inventory with {Items=role.Inventory.Items.Select(row=>row with {Uid=0}).ToImmutableArray()}};
                });
        GameCommandRegistry Registry(QuestOrdinaryCompletionTests.Fixture f,ServerLog log)
        {
            var commands=new GameCommandRegistry(); var projection=new QuestProjection(f.Catalog);
            new StackableActionHandler(f.Catalog,items.Items,projection,log,new CharacterLevelProjection(new CharacterStatComputer(characters),projection),skills).Register(commands);
            new QuestLifecycleHandler(f.Catalog,items.Items,items.GoldLimits,items.EpicPieces,log,projection).Register(commands);
            commands.Seal();return commands;
        }
    }

    static StoryQuest[] Definitions(byte stage) => [
        QuestEligibilityTests.Quest(100) with {MinimumLevel=stage==1 ? 50 : 75,Condition="[seeking]",IntData=[10165055,50],
            Rewards=[new(0,2058,null),new(0,4,null)],Definition=new([]) {Job="[all]",GrowType=1,JobChange=stage+1,
                PrerequisiteGroups=[[800],[900]],AcceptanceRewards=[new(0,2058,null),new(0,2,null)]}},
        QuestEligibilityTests.Quest(101) with {MinimumLevel=stage==1 ? 50 : 75,Condition="[clear map]",IntData=[12345],
            RewardType="[awakening type]",Rewards=[new(0,stage,null)],
            Definition=new([]) {Job="[all]",GrowType=1,JobChange=stage+1,PrerequisiteGroups=[[100]]}},
        QuestEligibilityTests.Quest(300) with {MinimumLevel=95,Condition="[seeking]",IntData=[stage==1 ? FirstTicket : SecondTicket,2]},
        QuestEligibilityTests.Quest(400) with {Condition="[clear quest]",IntData=[100]},
        QuestEligibilityTests.Quest(900),QuestEligibilityTests.Quest(800) ];
    static ActiveMainQuest Active(byte slot,ushort id,uint state=1) => new(slot,id,state) {AcceptanceId=Guid.NewGuid()};
    static bool PreservesLearning(CharacterLearningState before,CharacterLearningState after) =>
        before.Experience==after.Experience && before.BonusSp==after.BonusSp && before.BonusTp==after.BonusTp &&
        before.ExpertContractExpiresAt==after.ExpertContractExpiresAt && before.Trees.Zip(after.Trees).All(pair=>
            pair.First.Skills.All(skill=>pair.Second.Skills.Any(row=>row.Id==skill.Id && row.Slot==skill.Slot &&
                row.Level>=skill.Level && row.Command==skill.Command)) && pair.First.SameCombos(pair.Second));
    static long Receipts(QuestOrdinaryCompletionTests.Fixture f)
    {using var db=f.Open();using var command=db.CreateCommand();command.CommandText="SELECT count(*) FROM character_quest_skip_receipts";return (long)command.ExecuteScalar()!;}
    static Packet Use(uint action,ushort slot=65)
    {byte[] body=new byte[19];BinaryPrimitives.WriteUInt16LittleEndian(body,slot);BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7),action);return new(1,515,body);}
    static Packet Giveup(ushort id)
    {byte[] body=new byte[4];BinaryPrimitives.WriteUInt16LittleEndian(body,32);BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2),id);return new(1,32,body);}
    static GameConnectionContext Context(QuestOrdinaryCompletionTests.Fixture f,Stream output,GameConnectionSender sender,CancellationTokenSource stop) =>
        new(new(92,IPAddress.Loopback,output,stop),f.Owner,f.Session,sender);
    static async Task RejectAsync<T>(Func<Task> action) where T:Exception
    {try {await action();}catch(T) {That(true,"觉醒券提交后ACK发送故障");return;}throw new InvalidOperationException("FAIL expected "+typeof(T).Name);}
    sealed class ControlledOutput : MemoryStream
    {
        int writes;public int FailOn {get;init;}public List<byte[]> Frames {get;}=[];
        public IEnumerable<ushort> Opcodes=>Frames.Select(row=>BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(1)));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)
        {if(++writes==FailOn) throw new IOException("觉醒券发布故障");Frames.Add(buffer.ToArray());return ValueTask.CompletedTask;}
    }
}
