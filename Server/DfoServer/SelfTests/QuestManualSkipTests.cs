using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
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

internal static class QuestManualSkipTests
{
    public static void RunCore()
    {
        int checks = Count;
        That(new byte[] {1,2,90,91,94,95}.Select(QuestQuickClearPlan.ManualCutoff).SequenceEqual([0,1,89,89,89,94]),
            "手册使用本版实际等级截止，95不是旧服固定89");
        That(QuestSkipRequests.TryManual([0,0,0,0],out var zero) && zero.ItemContext == 0 &&
            QuestSkipRequests.TryManual([255,255,255,255],out var item) && item.ItemContext == uint.MaxValue &&
            !QuestSkipRequests.TryManual([0,0,0],out _) && !QuestSkipRequests.TryManual([0,0,0,0,0],out _) &&
            QuestSkipRequests.TryFinishManual([]) && !QuestSkipRequests.TryFinishManual([0]),
            "1430完整u32保留物品上下文；1457精确空body，无questID转换");
        Bytes(QuestSkipPackets.Questions([500,100]),[2,244,1,0,0,100,0,0,0],"1381保持真实问题ID原序，无token");
        TestCheck.Reject<InvalidDataException>(() => QuestSkipPackets.Questions([]),"空1381不能代替成功ACK");
        var source = new Character(1,0,"Manual",0,new(1,0,1,1),null)
        {
            Level = 20, CompletedQuests = [900], QuestAnswers = ImmutableDictionary<int,byte>.Empty.Add(900,1),
            ActiveMainQuests = [Active(0,100),Active(1,300),Active(2,103)]
        };
        var quests = new QuestCatalog(PlainDefinitions().ToDictionary(row => row.Id)); var operation = Guid.NewGuid();
        That(QuestQuickClearPlan.TryCreateManual(source,operation,quests,ImmutableDictionary<int,byte>.Empty,out var plan,out string reason),
            "无question手册可规划真实主线闭包: "+reason);
        That(plan is { IsManual:true,AcceptanceId:var empty,ParentKey:null,QuestionQuestId:null } && empty == Guid.Empty &&
            plan.OperationId == operation && plan.PendingQuestions.IsEmpty && plan.ScopeQuestIds.SequenceEqual([100,101,110,111]),
            "手册仅用操作身份，不伪造question/AcceptanceId，闭包含显式零奖励子任务");
        That(plan!.RemovedActive.Select(row => row.QuestId).SequenceEqual(new ushort[] {100}) &&
            plan.ActiveAfter.Single(row => row.QuestId == 300).ConditionState == 0 &&
            plan.ActiveAfter.Any(row => row.QuestId == 103) && !plan.CompletedAfter.Contains(102) && !plan.CompletedAfter.Contains(105),
            "截止、冲突与side任务保持，重算无关clear-quest目标但不完成它");
        That(plan.AnswersAfter[900] == 1 && source.ActiveMainQuests.Length == 3 && !source.CompletedQuests.Contains(100),
            "规划保留已有回答，不修改原角色");
        foreach (byte job in Enumerable.Range(0,16).Select(value => (byte)value))
            That(QuestQuickClearPlan.TryCreateManual(source with { Job=job },operation,quests,ImmutableDictionary<int,byte>.Empty,out _,out _),
                "手册普通资格不硬编码职业 "+job);
        That(!QuestQuickClearPlan.TryCreateManual(source with { Level=1 },operation,quests,ImmutableDictionary<int,byte>.Empty,out _,out _),
            "Lv1无合法截止，不能自动清整链");
        var full = source with { ActiveMainQuests = Enumerable.Range(0,30).Select(index => Active((byte)index,(ushort)(1000+index))).ToArray() };
        That(QuestQuickClearPlan.TryCreateManual(full,operation,quests,ImmutableDictionary<int,byte>.Empty,out var fullPlan,out _) &&
            fullPlan!.ActiveAfter.Length == 30,"手册不占普通30槽");
        var branching = new QuestCatalog(QuestionDefinitions(chained:true).ToDictionary(row => row.Id));
        var emptyAnswers = ImmutableDictionary<int,byte>.Empty;
        That(QuestQuickClearPlan.TryCreateManual(source,operation,branching,emptyAnswers,out var first,out _) &&
            first!.PendingQuestions.SequenceEqual([100]),"未接question按真实可达入口排队，无默认答案");
        var one = emptyAnswers.Add(100,0);
        That(QuestQuickClearPlan.TryCreateManual(source,operation,branching,one,out var second,out _) &&
            second!.PendingQuestions.SequenceEqual([200]) && !second.CompletedAfter.Contains(102),"回答后显露第二批问题，不写互斥分支");
        That(QuestQuickClearPlan.TryCreateManual(source,operation,branching,one.Add(200,1),out var final,out _) &&
            final!.PendingQuestions.IsEmpty && final.CompletedAfter.Contains(202) && !final.CompletedAfter.Contains(201),
            "多轮真实答案产生有界闭包，未选分支不完成");
        That(!QuestQuickClearPlan.TryCreateManual(source,operation,branching,emptyAnswers.Add(100,2),out _,out _) &&
            !QuestQuickClearPlan.TryCreateManual(source,operation,branching,emptyAnswers.Add(999,0),out _,out _),
            "越界或不属于可达问题的回答拒绝，不占位成功");
        foreach (var restricted in new[] {
            quests.Get(100) with { Definition = quests.Get(100).Definition! with { AccountQuest=true } },
            quests.Get(100) with { Definition = quests.Get(100).Definition! with { JobChange=1 } },
            quests.Get(100) with { RewardType="[tp]" },
            quests.Get(100) with { Definition=new([]) { Diagnostics=["失效资源fixture"] } } })
        {
            That(!QuestQuickClearPlan.TryCreateManual(source,operation,new QuestCatalog(new() { [100]=restricted }),emptyAnswers,out _,out _),
                "只有账号、职业、专用状态或坏定义时中立拒绝，不借手册写普通完成");
            var independent = QuestEligibilityTests.Quest(600);
            var gated = QuestEligibilityTests.Quest(601) with { Definition=new([]) { PrerequisiteGroups=[[100]] } };
            var mixed = new QuestCatalog(new() { [100]=restricted,[600]=independent,[601]=gated });
            That(QuestQuickClearPlan.TryCreateManual(source,operation,mixed,emptyAnswers,out var ordinary,out _) &&
                ordinary!.ScopeQuestIds.SequenceEqual([600]) && ordinary.ActiveAfter.Any(row => row.QuestId == 100) &&
                !ordinary.CompletedAfter.Contains(100) && !ordinary.CompletedAfter.Contains(601),
                "专用/失效候选不挡独立普通主线，自己和被其前置阻挡的后继保持未完成");
        }
        Console.WriteLine($"PASS {Count-checks} 手册截止、普通资格、真实问题、无奖励与纯协议断言");
    }

    public static async Task RunAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        SkillLearningCatalog? sharedSkills = null)
    {
        int checks = Count;
        QuestCatalogTests.RunClosingTags();
        VerifyAreaScope();
        var skills = sharedSkills ?? global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills); var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        string logRoot = Path.Combine(Path.GetTempPath(),"S5A9-manual-skip-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logRoot);
        try
        {
            using var log = new ServerLog(logRoot);
            var realQuests = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
            var bell=realQuests.Get(3992);
            var bellCharacter=new Character(1,0,"Bell",0,towns.Room,null) {Level=95,GrowType=0x25};
            That(bell.Definition is {Diagnostics.IsEmpty:true} && bell.Prerequisites.SequenceEqual([3991]) &&
                !QuestEligibility.Evaluate(bellCharacter,bell,realQuests).Eligible &&
                QuestEligibility.Evaluate(bellCharacter with {CompletedQuests=[3991]},bell,realQuests).Eligible,
                "真实3992重复闭合兼容普通接取，仍须先完成3991");
            foreach(var state in new (byte Job,byte Grow)[]{(0,0x11),(0,0x21),(13,0x13),(13,0x23),(9,0x10),(10,0x20)})
            {
                var gm=new Character(1,0,"GmStory",0,towns.Room,null) {Job=state.Job,GrowType=state.Grow,Level=95};
                That(QuestQuickClearPlan.TryCreateManual(gm,Guid.NewGuid(),realQuests,ImmutableDictionary<int,byte>.Empty,out var reached,out _,towns) &&
                    reached!.CompletedAfter.Contains(3155) && reached.CompletedAfter.Contains(3427) &&
                    reached.ProfessionGrowType is null && reached.EquipmentSlotFlagsAfter==gm.EquipmentSlotFlags &&
                    ReferenceEquals(reached.TargetLearning,gm.Learning),"GM已有转职/觉醒、无对应任务记录仍衔接主线，不改变职业技能或槽位");
            }
            var transferred=new Character(1,0,"GmTransfer",0,towns.Room,null) {GrowType=1,Level=95};
            That(QuestQuickClearPlan.TryCreateManual(transferred,Guid.NewGuid(),realQuests,ImmutableDictionary<int,byte>.Empty,out var transferOnly,out _,towns) &&
                transferOnly!.CompletedAfter.Contains(3155) && transferOnly.CompletedAfter.Contains(3427) &&
                transferOnly.CompletedAfter.Contains(3368) && transferOnly.ProfessionGrowType is null,
                "未觉醒角色可完成对应主线记录并接续剧情，实际一觉状态保持");
            var advanced=new Character(1,0,"StoryBridge",0,towns.Room,null) {Job=13,GrowType=0x23,Level=95,CompletedQuests=[3356]};
            That(QuestEligibility.Evaluate(advanced,realQuests.Get(3418),realQuests) is {Eligible:true,Visible:true} &&
                !QuestEligibility.Evaluate(advanced,realQuests.Get(3421),realQuests).Eligible,
                "真实PVF已二觉魔枪士普通3418主线可接，专用3421觉醒不被重新完成");
            That(QuestQuickClearPlan.TryCreateManual(advanced,Guid.NewGuid(),realQuests,ImmutableDictionary<int,byte>.Empty,out var bridgePlan,out _,towns),"真实转职主线可通过新手册跳过接续");
            var bridge=bridgePlan!.Interval;
            That(bridge.Any(quest=>quest.Id==3418) && bridge.Any(quest=>quest.Id==3427) &&
                bridge.All(quest=>(quest.Grade is "[epic]" or "[sub]" || QuestRelationProgress.IsCascadeMarker(quest)) &&
                    quest.RewardType is (null or "[item]") &&
                    (quest.Definition?.JobChange==0 || QuestEligibility.TransferredStoryAlternative(quest,realQuests,advanced.Job))),
                "新主线跳过完成普通转职/觉醒衔接及西海岸码头3427，不增加职业奖励或槽位任务");
            var bridgeCompleted=advanced.CompletedQuests.Concat(bridge.Select(quest=>quest.Id)).ToHashSet();
            That(!towns.IsPosition(new(40,1,1689,146),advanced.CompletedQuests.ToHashSet()) &&
                towns.IsPosition(new(40,1,1689,146),bridgeCompleted),"真实西海岸码头入口由不可走变为可走");
            VerifyProfessionSupplements(realQuests,towns);
            await QuestSkipEffectsTests.RunAsync(towns,characters,items,skills,growth,parameters,log,realQuests);
            // 所有主线已有完成记录时，区域入场引用不能再授权独立支线或外传。
            var mainCompleted = realQuests.All.Where(quest => quest.Grade == "[epic]").Select(quest => quest.Id).Order().ToArray();
            using (var f = new QuestOrdinaryCompletionTests.Fixture(towns,characters,skills,growth,parameters,items,realQuests.Get(10100),
                materialCount:0,definitions:realQuests.All.ToArray(),active:[],configure:candidate => candidate with
                {
                    Level=95,CompletedQuests=mainCompleted,InitialLearning=SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,95,
                        candidate.InitialLearning with { Experience=growth.GetLevelStartExperience(95) },skills)
                }))
            {
                int[] access = [2670,2941,6170,6928,8664,8665,8672,8703,8709,10100,6880,4391,4539,2002,2900,6080,6081,10112];
                var before = f.Read();
                That(f.Session.PrepareManualQuestSkip(f.Catalog)?.Prepared is null && Same(before,f.Read()) &&
                    access.All(id=>!f.Read().CompletedQuests.Contains(id)) && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts")==0,
                    "真实PVF主线均完成后，区域支线、外传、普通及星空深渊任务不再由主线手册完成");
            }
            using (var f = Fixture([Question(400),Branch(501,400,1),QuestEligibilityTests.Quest(500) with { MinimumLevel=17 },
                QuestEligibilityTests.Quest(999) with { Grade="[invisible]",IntData=[-1],Definition=new([new("synchronization account quest",[new(0,1,null)],[])]) },
                QuestEligibilityTests.Quest(10100) with { Grade="[side]",MinimumLevel=17,Definition=new([]) { PrerequisiteGroups=[[500]] } }],
                [Active(0,400,QuestQuestionProgress.Unanswered)]))
            {
                var before = f.Read();
                var receipt = f.Session.QuickClearQuest(1007,f.Catalog,Connections())!;
                That(receipt.Commit is not null && receipt.Commit.Plan.Interval.Select(row => row.Id).SequenceEqual([500]) &&
                    receipt.Commit.Plan.AreaQuests.IsEmpty &&
                    !f.Read().CompletedQuests.Contains(10100) && Assets(before) == Assets(f.Read()),
                    "1714只完成原主线章节区间，不扩展同等级区域支线");
                That(f.Repository.CommitQuestQuickClear(f.Owner,receipt.Commit!.Plan) is { Duplicate:true,ProjectionCurrent:true } &&
                    Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 1,"1714主线范围沿冻结回执精确只读恢复");
            }
            using (var f = Fixture(PlainDefinitions(),[Active(0,100),Active(1,300),Active(2,103)]))
            {
                var before = f.Read(); using var output = new ControlledOutput(); using var stop = new CancellationTokenSource();
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop); var commands = Registry(f);
                That(await commands.DispatchAsync(context,new(1,1430,[0,0,0,0])) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {1430}) && Same(before,f.Read()),"无问题1430仅ACK且不提前写完成或revision");
                Bytes(output.Frames.Single().AsSpan(16),[1],"1430成功无旧13B或其它tail");
                var prepared = f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared!;
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,new(1,1430,[0,0,0,0])) == CommandResult.Handled &&
                    f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared!.OperationId == prepared.OperationId,
                    "重复1430沿同一冻结操作，不重抽范围");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,new(1,1457,[])) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {574,21,356}),"真实1457才提交，574→21→356，无猜测ACK1457");
                var after = f.Read();
                That(after.Revision == before.Revision+1 && after.CompletedQuests.SequenceEqual([100,101,110,111]) &&
                    Assets(after) == Assets(before) && after.ActiveMainQuests.Single(row => row.QuestId == 300).ConditionState == 0 &&
                    after.ActiveMainQuests.Any(row => row.QuestId == 103) && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 1,
                    "赠品/寻物/普通XP Gold奖励不发不扣，完成及父状态仅一次原子提交");
                That(Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts WHERE operation_kind='Manual' AND acceptance_id IS NULL AND parent_key IS NULL AND question_quest IS NULL") == 1,
                    "手册回执使用操作身份，真实无question/普通接取身份字段");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,new(1,1457,[])) == CommandResult.Handled && output.Frames.Count == 0 && Same(after,f.Read()),
                    "重复1457只忽略已消费上下文，不二次完成/投影");
                That(f.Repository.CommitQuestQuickClear(f.Owner,prepared) is { Duplicate:true,ProjectionCurrent:true } && Same(after,f.Read()),
                    "持久手册操作回执精确只读恢复");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(Same(after,f.Session.Selected!),"重选回放真实完成及资产，无补奖励");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,new(1,1430,[100,0,0,0])) == CommandResult.Unhandled && output.Frames.Count == 0 && Same(after,f.Read()),
                    "非零1430物品上下文保持未接，不误当任务100");
            }
            using (var f = Fixture(QuestionDefinitions(chained:false),[]))
            {
                var before = f.Read(); using var output = new ControlledOutput(); using var stop = new CancellationTokenSource();
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop); var commands = Registry(f);
                That(await commands.DispatchAsync(context,new(1,1430,[0,0,0,0])) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {1381}) && Same(before,f.Read()),"有问题只发1381，不提前ACK1430触发1457");
                Bytes(output.Frames.Single().AsSpan(16),[2,100,0,0,0,200,0,0,0],"1381原序批量问题，无普通接取槽");
                var prepared = f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared!;
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Answers(new(200,0),new(100,1))) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {1431}) && Same(before,f.Read()),"1431乱序拒绝，不修改冻结问题或存档");
                Bytes(output.Frames.Single().AsSpan(16),[0,0,0],"1431拒绝使用已确认公共失败前缀");
                output.Frames.Clear(); var answers = Answers(new(100,1),new(200,0));
                That(await commands.DispatchAsync(context,answers) == CommandResult.Handled && output.Opcodes.SequenceEqual(new ushort[] {1431,574,21,356}),
                    "最终1431答案先原子提交再ACK→574→21→356，不等待客户端1457");
                Bytes(output.Frames[0].AsSpan(16),[1],"1431成功无业务tail");
                That(f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared!.OperationId == prepared.OperationId,"问题回答复用原操作身份");
                var after = f.Read();
                That(after.CompletedQuests.SequenceEqual([100,102,200,201]) && after.QuestAnswers[100] == 1 && after.QuestAnswers[200] == 0 &&
                    after.ActiveMainQuests.Length == 0 && Assets(after) == Assets(before) && after.Revision == before.Revision+1,
                    "实际零基答案与选择分支同事务，无未选分支/假槽/奖励");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,answers) == CommandResult.Handled && output.Opcodes.SequenceEqual(new ushort[] {1431,574,21,356}) &&
                    Same(after,f.Read()) && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 1,
                    "重复最终1431沿已安装同一提交恢复ACK和投影，无重规划或存档写入");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,new(1,1457,[])) == CommandResult.Handled && output.Frames.Count == 0 && Same(after,f.Read()),
                    "mode1在最终回答后仍可发送1457尾包，只消费已提交上下文，不二次写库或投影");
            }
            using (var f = Fixture(QuestionDefinitions(chained:true),[]))
            {
                var before = f.Read(); using var output = new ControlledOutput(); using var stop = new CancellationTokenSource();
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop); var commands = Registry(f);
                await commands.DispatchAsync(context,new(1,1430,[0,0,0,0])); output.Frames.Clear();
                That(await commands.DispatchAsync(context,Answers(new QuestSkipAnswer(100,0))) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {1381}) && Same(before,f.Read()),
                    "第一批1431显露后继问题只发1381，不提前提交部分完成或回答");
                Bytes(output.Frames.Single().AsSpan(16),[1,200,0,0,0],"第二批1381保持真实问题ID");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Answers(new QuestSkipAnswer(200,1))) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {1431,574,21,356}) &&
                    f.Read().CompletedQuests.SequenceEqual([100,101,200,202]),"最后一批1431一次提交完整多轮实际答案和选择分支");
            }
            foreach (bool quick in new[] { false,true })
            foreach (bool completion in new[] { false,true })
            {
                var observed = new InterleavedRewards([]);
                var definitions = new[] {
                    QuestEligibilityTests.Quest(100) with { Rewards = quick ? [] : observed },
                    Question(400) with { MinimumLevel=20 },
                    QuestEligibilityTests.Quest(500) with { MinimumLevel=20, Rewards = quick ? observed : [] },
                    QuestEligibilityTests.Quest(999) with { Grade="[invisible]",IntData=[-1],
                        Definition=new([new("synchronization account quest",[new(0,1,null)],[])]) },
                    QuestEligibilityTests.Quest(600) with { Grade="[side]" },
                    QuestEligibilityTests.Quest(601) with { Grade="[side]",Condition="[seeking]",IntData=[10165055,1],
                        Rewards=[new(0,2058,null),new(0,1,null)],Attributes=["not give exp quest"] }
                };
                using var f = Fixture(definitions,quick ? [Active(0,400,QuestQuestionProgress.Unanswered),Active(1,601)] : [Active(0,601)],capturePlans:true);
                var before = f.Session.Selected!;
                string table = completion ? "character_quest_completion_receipts" : "character_quest_lifecycle_receipts";
                f.Sql($"CREATE TRIGGER interleaved_quest_abort AFTER INSERT ON {table} BEGIN SELECT RAISE(ABORT,'interleaved rollback'); END");
                observed.BeforeRead = () =>
                {
                    // 资源读取发生在跳过入口释放owner后，精确插入另一业务的失败提交。
                    if (completion) TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue,601),"跳过规划期间领奖SQL晚期回滚");
                    else TestCheck.Reject<SqliteException>(() => f.Change(600,QuestLifecycleAction.Accept),"跳过规划期间接取SQL晚期回滚");
                    f.Sql("DROP TRIGGER interleaved_quest_abort");
                };
                TestCheck.Reject<InvalidOperationException>(() => Prepare(),"锁外跳过规划返回后必须重新检查另一路未决提交");
                That(observed.Calls == 1 && ReferenceEquals(before,f.Session.Selected) && Same(before,f.Read()) &&
                    Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 0 && Scalar(f,$"SELECT count(*) FROM {table}") == 0,
                    "确定性交错只触发一次；跳过未入库，原Selected、资产、任务与revision完整保留");
                if (completion)
                {
                    var original = f.Lost!.FirstPlan!; var recovered = f.Finish(ushort.MaxValue,601);
                    That(recovered.Receipt is { Commit.Duplicate: false } && f.Lost.SamePlan && f.Lost.Calls == 2 &&
                        recovered.Receipt.Commit.AcceptanceId == original.AcceptanceId && Scalar(f,$"SELECT count(*) FROM {table}") == 1,
                        "跳过被拒后原领奖冻结Plan及Acceptance只提交一次");
                }
                else
                {
                    var recovered = f.Change(600,QuestLifecycleAction.Accept)!;
                    That(recovered.Commit is { Duplicate: false } && f.Lost!.SameLifecyclePlan && Scalar(f,$"SELECT count(*) FROM {table}") == 1,
                        "跳过被拒后原接取冻结Plan与Operation仍可提交一次");
                }
                var prepared = Prepare();
                That(quick ? prepared?.Commit is { Duplicate: false } : prepared?.Prepared is not null &&
                    f.Session.FinishManualQuestSkip()?.Commit is { Duplicate: false },
                    "原事务恢复后新的显式跳过可以正常提交，不留下伪pending");

                QuestQuickClearReceipt? Prepare() => quick ? f.Session.QuickClearQuest(1007,f.Catalog,Connections()) :
                    f.Session.PrepareManualQuestSkip(f.Catalog);
            }
            using (var f = Fixture(QuestionDefinitions(chained:false),[],useCycles:true))
            {
                var before = f.Read(); var original = f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared!;
                QuestSkipAnswer[] answers = [new(100,1),new(200,0)];
                f.Sql("CREATE TRIGGER manual_answer_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'answer rollback'); END");
                TestCheck.Reject<SqliteException>(() => f.Session.AnswerManualQuestSkip(answers,f.Catalog),"最终1431原子提交末段SQL失败");
                var frozen = f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared!;
                That(Same(before,f.Read()) && frozen.OperationId == original.OperationId && frozen.PendingQuestions.IsEmpty &&
                    frozen.ManualAnswers[100] == 1 && frozen.ManualAnswers[200] == 0 &&
                    f.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1)) is null,
                    "最终答案SQL失败保留原操作和全部答案，未写完成且恢复期间阻止切日");
                f.Sql("DROP TRIGGER manual_answer_abort");
                var persisted = f.Repository.CommitQuestQuickClear(f.Owner,frozen);
                var restored = f.Session.AnswerManualQuestSkip(answers,f.Catalog);
                That(restored?.Commit is { Duplicate:true,ProjectionCurrent:true } && restored.Commit.Plan.Snapshot == frozen.Snapshot &&
                    Same(persisted.Character,f.Read()) && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 1,
                    "最终1431在提交返回丢失后按冻结答案和OperationId恢复，未重算另一个范围");
                That(f.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1)) is not null,
                    "1431已安装Commit缓存不再阻止切日");
                That(f.Session.AnswerManualQuestSkip(answers,f.Catalog) is { Commit:null,Prepared:null },
                    "切日真实更新Selected后清已安装缓存，旧答案不再重放过期完成投影");
            }
            using (var f = Fixture(QuestionDefinitions(chained:false),[]))
            {
                var before = f.Read(); var original = f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared!;
                var commands = Registry(f); var answers = Answers(new(100,1),new(200,0));
                using (var broken = new ControlledOutput { FailOn=1 })
                using (var stop = new CancellationTokenSource())
                {
                    await using var sender = new GameConnectionSender(f.Owner,broken,stop,0,(_,_,_,_,_) => { });
                    await RejectAsync<IOException>(() => commands.DispatchAsync(Context(f,broken,sender,stop),answers),"提交后ACK1431发送失败");
                }
                var committed = f.Read();
                using var output = new ControlledOutput(); using var retryStop = new CancellationTokenSource();
                await using var retrySender = new GameConnectionSender(f.Owner,output,retryStop,0,(_,_,_,_,_) => { });
                That(await commands.DispatchAsync(Context(f,output,retrySender,retryStop),answers) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {1431,574,21,356}) && Same(committed,f.Read()) &&
                    committed.Revision == before.Revision+1 && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 1 &&
                    f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared!.OperationId == original.OperationId,
                    "ACK1431丢失后的同会话重发恢复已安装操作，全部状态仅提交一次");
            }
            foreach (bool installed in new[] {false,true})
            using (var f = Fixture(QuestionDefinitions(chained:false).Concat(new[] {
                Question(400) with { MinimumLevel=20 },QuestEligibilityTests.Quest(500) with { MinimumLevel=20 },
                QuestEligibilityTests.Quest(999) with { Grade="[invisible]",IntData=[-1],
                    Definition=new([new("synchronization account quest",[new(0,1,null)],[])]) }
                }).ToArray(),[Active(0,400,QuestQuestionProgress.Unanswered)]))
            {
                f.Session.PrepareManualQuestSkip(f.Catalog);
                if (installed) f.Session.AnswerManualQuestSkip([new(100,1),new(200,0)],f.Catalog);
                var before = f.Read();
                var quick = f.Session.QuickClearQuest(1007,f.Catalog,Connections());
                That(quick?.Commit is { Duplicate:false } && quick.Character.CompletedQuests.Contains(400) &&
                    quick.Character.CompletedQuests.Contains(500) && quick.Character.CompletedQuests.Contains(999) &&
                    quick.Character.Revision == before.Revision+1 &&
                    Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == (installed ? 2 : 1),
                    "明确1714替换未提交或已安装手册缓存，仍沿真实活动question及资源区间提交: "+installed);
            }
            using (var f = Fixture(QuestionDefinitions(chained:false),[],useCycles:true))
            {
                var before = f.Read(); var prepared = f.Session.PrepareManualQuestSkip(f.Catalog)!;
                var next = before.Position with { X = checked((ushort)(before.Position.X+1)) };
                That(f.Session.Move(new(next.X,next.Y,0,0,0,0)) && f.Session.Selected!.Revision == before.Revision+1,
                    "未提交手册问题UI不阻止普通步行和完整Selected更新");
                var cycle = f.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1));
                That(cycle is not null && cycle.Character.QuestCycleDay > before.QuestCycleDay &&
                    cycle.Character.Revision == before.Revision+2 && !f.Session.CanPublishQuestQuickClear(prepared),
                    "仅prepared不阻止每日切换；切日后原快照及问题投影失效");
                That(f.Session.FinishManualQuestSkip() is null && f.Read().CompletedQuests.Length == 0 &&
                    Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 0,
                    "旧1457不能把未提交问题准备换成另一个范围或补完成");
                var replacement = f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared!;
                That(replacement.OperationId != prepared.Prepared!.OperationId && replacement.Expected.QuestCycleDay == cycle!.Character.QuestCycleDay,
                    "只有新的显式手册准备才建立新操作，不用失效原计划重抽");
            }
            using (var f = Fixture(PlainDefinitions(),[],useCycles:true))
            {
                var before = f.Read(); var plan = f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared!;
                f.Sql("CREATE TRIGGER manual_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'manual rollback'); END");
                TestCheck.Reject<SqliteException>(() => f.Session.FinishManualQuestSkip(),"手册回执末段SQL失败");
                That(Same(before,f.Read()) && ReferenceEquals(f.Session.Selected,plan.Expected) && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 0,
                    "SQL失败回滚全部完成/答案/状态/revision，Session不安装");
                That(f.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1)) is null,
                    "实际提交待恢复时继续阻止切日，不让原回执的周期来源失效");
                f.Sql("DROP TRIGGER manual_abort");
                var persisted = f.Repository.CommitQuestQuickClear(f.Owner,plan);
                That(ReferenceEquals(f.Session.Selected,plan.Expected),"模拟已提交但结果未安装，原pending仍冻结");
                var restored = f.Session.FinishManualQuestSkip();
                That(restored?.Commit is { Duplicate:true,ProjectionCurrent:true } && Same(persisted.Character,f.Read()) &&
                    Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 1,"1457按原OperationId恢复已提交回执，不重复发奖或写revision");
                That(f.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1)) is not null,
                    "提交恢复并安装后释放pending，随后允许正常切日");
            }
            using (var f = Fixture(PlainDefinitions(),[],useCycles:true))
            {
                var plan = f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared!;
                f.Sql("CREATE TRIGGER manual_pending_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'pending rollback'); END");
                TestCheck.Reject<SqliteException>(() => f.Session.FinishManualQuestSkip(),"建立已开始提交的手册恢复状态");
                f.Sql("DROP TRIGGER manual_pending_abort");
                var next = plan.Expected.Position with { X=checked((ushort)(plan.Expected.Position.X+1)) };
                That(!f.Session.Move(new(next.X,next.Y,0,0,0,0)) && ReferenceEquals(plan.Expected,f.Session.Selected) && Same(plan.Expected,f.Read()),
                    "恢复状态阻止位置写入，保留原Selected、revision和存档供精确重试");
                That(ReferenceEquals(f.Session.PrepareManualQuestSkip(f.Catalog)?.Prepared,plan) &&
                    f.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1)) is null &&
                    Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 0 && f.Read().CompletedQuests.Length == 0,
                    "被拒移动不会清除已开始提交的pending；原手册准备与切日门禁保持");
                var restored = f.Session.FinishManualQuestSkip();
                That(restored?.Commit is { Duplicate:false,ProjectionCurrent:true } && ReferenceEquals(restored.Commit.Plan,plan) &&
                    Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 1,
                    "被拒移动后原OperationId和冻结计划可完整恢复一次");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(f.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1)) is not null,
                    "重选恢复已提交存档，真实持久状态仍可正常切日");
            }
            using (var f = Fixture(PlainDefinitions(),[],useCycles:true))
            {
                var plan = f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared!;
                f.Sql("UPDATE characters SET revision=revision+1");
                TestCheck.Reject<InvalidOperationException>(() => f.Session.FinishManualQuestSkip(),"旧revision不能提交手册规划");
                That(Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 0 && f.Read().CompletedQuests.Length == 0,
                    "过期规划不完成任何任务");
                That(f.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1)) is null,
                    "失效手册提交仍保留未决状态，不能让切日覆盖原恢复上下文");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(f.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1)) is not null &&
                    Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == 0 && f.Read().CompletedQuests.Length == 0,
                    "明确重选结束失效pending，按持久revision恢复且不补做旧手册");
            }

            QuestOrdinaryCompletionTests.Fixture Fixture(StoryQuest[] definitions,ActiveMainQuest[] active,bool useCycles=false,bool capturePlans=false) =>
                new(towns,characters,skills,growth,parameters,items,definitions[0],materialCount:5,definitions:definitions,active:active,
                    configure:candidate => candidate with
                    {
                        Level=20,InitialLearning=SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,20,
                            candidate.InitialLearning with { Experience=growth.GetLevelStartExperience(20) },skills)
                    },useCycles:useCycles,loseResult:capturePlans);
            GameCommandRegistry Registry(QuestOrdinaryCompletionTests.Fixture fixture)
            {
                var commands = new GameCommandRegistry();
                new QuestQuickClearHandler(fixture.Catalog,QuestConnectionCatalog.Parse([],_ => ""),new QuestProjection(fixture.Catalog),log).Register(commands);
                commands.Seal(); return commands;
            }
        }
        finally { TestFiles.DeleteDirectory(logRoot,true); }
        Console.WriteLine($"PASS {Count-checks} 手册真实Handler、1381/1431/1457、SQL原子与恢复断言（非主线实机）");
    }

    static void VerifyProfessionSupplements(QuestCatalog quests,TownCatalog towns)
    {
        string[] jobs=["[swordman]","[fighter]","[gunner]","[mage]","[priest]","[at gunner]","[thief]","[at fighter]",
            "[at mage]","[demonic swordman]","[creator mage]","[at swordman]","[knight]","[demonic lancer]","[at priest]","[gun blader]"];
        for(byte job=0;job<jobs.Length;job++)
        {
            var source=new Character(1,0,"Supplement",job,towns.Room,null) {Job=job,Level=95};
            That(QuestQuickClearPlan.TryCreateManual(source,Guid.NewGuid(),quests,ImmutableDictionary<int,byte>.Empty,out var plan,out _,towns) &&
                plan!.CompletedAfter.Contains(3155) && plan.CompletedAfter.Contains(3427) && plan.ProfessionGrowType is null &&
                !plan.Interval.Any(q=>q.RewardType=="[grow type]"),"未转职主线跳过使用普通衔接，不猜转职分支: "+jobs[job]);
            var after=source with {CompletedQuests=plan!.CompletedAfter.ToArray()};
            int change=job is 9 or 10 ? 2 : 1;
            That(quests.All.Any(q=>q.Grade!="[epic]" && q.Definition?.JobChange==change &&
                QuestEligibility.Evaluate(after,q,quests).Visible),"主线跳过后独立转职/自觉入口仍可见: "+jobs[job]);
        }
        foreach(var main in quests.All.Where(q=>q.Grade=="[epic]" && q.RewardType=="[awakening type]" &&
            q.Rewards is [{Value:1}] && q.Definition is {JobChange:2,GrowType:>0}))
        {
            var definition=main.Definition!; byte job=checked((byte)Array.IndexOf(jobs,definition.Job));
            byte branch=checked((byte)definition.GrowType);
            var source=new Character(1,0,"Supplement",job,towns.Room,null) {Job=job,GrowType=branch,Level=95};
            That(QuestQuickClearPlan.TryCreateManual(source,Guid.NewGuid(),quests,ImmutableDictionary<int,byte>.Empty,out var plan,out _,towns) &&
                plan!.CompletedAfter.Contains(main.Id) && plan.CompletedAfter.Contains(3427) && plan.ProfessionGrowType is null &&
                ReferenceEquals(plan.TargetLearning,source.Learning),"一觉主线只记完成，不改职业技能: "+main.Id);
            var after=source with {CompletedQuests=plan!.CompletedAfter.ToArray()};
            That(quests.All.Any(q=>q.Grade!="[epic]" && q.Definition?.JobChange==2 && QuestEligibility.Evaluate(after,q,quests).Visible) &&
                quests.All.Any(q=>q.Grade!="[epic]" && q.Definition?.JobChange==3 &&
                    QuestEligibility.Evaluate(after with {GrowType=(byte)(branch|0x10)},q,quests).Visible),
                "各职业一觉补充入口不与主线完成互斥，二觉入口仍要求实际一觉: "+main.Id);
        }
        That(!quests.All.Any(q=>q.Grade=="[epic]" && q.RewardType=="[awakening type]" && q.Rewards is [{Value:2}]),
            "匹配PVF二觉奖励链全部在主线之外");
    }

    static void VerifyAreaScope()
    {
        var map = new TownMap("fixture/area.map",[new(new(0,0,1000,1000),[],[])],[],false,[]);
        var towns = new TownCatalog(new()
        {
            [(38,0)] = new(38,0,1,[600,601,603,604,605,606],[],[608],null,map,[],[])
        },new(38,0,10,10),new(38,0,10,10),new(0,[],[]),[],new Dictionary<uint,uint[]>(),new Dictionary<int,TownDungeonMap>()
        {
            [1] = new(1,"fixture/worldmap",[new(1,609,true)])
        });
        var definitions = new[]
        {
            QuestEligibilityTests.Quest(100),QuestEligibilityTests.Quest(610) with { MinimumLevel=19 },
            QuestEligibilityTests.Quest(600) with { MinimumLevel=20,Definition=new([]) { PrerequisiteGroups=[[610]] } },
            QuestEligibilityTests.Quest(601) with { Grade="[episode]",MinimumLevel=19 },
            QuestEligibilityTests.Quest(602) with { Grade="[side]" },
            QuestEligibilityTests.Quest(603) with { Grade="[side]",MinimumLevel=21 },
            QuestEligibilityTests.Quest(604) with { Grade="[side]",Definition=new([]) { Job="[mage]" } },
            QuestEligibilityTests.Quest(605) with { Grade="[side]",Definition=new([]) { AccountQuest=true } },
            QuestEligibilityTests.Quest(606) with { Grade="[side]",Definition=new([new("unconfirmed",[],[])]) },
            QuestEligibilityTests.Quest(608) with { Grade="[side]" },QuestEligibilityTests.Quest(609) with { Grade="[side]" }
        };
        var source = new Character(1,0,"Access",0,new(38,0,10,10),null) { Level=20 };
        var catalog=new QuestCatalog(definitions.ToDictionary(row=>row.Id));
        That(QuestQuickClearPlan.TryCreateManual(source,Guid.NewGuid(),catalog,
            ImmutableDictionary<int,byte>.Empty,out var plan,out _,towns) && plan!.ScopeQuestIds.SequenceEqual([100,610]),
            "主线区域任务遵守手册截止，入场引用不扩展独立支线或外传");
        That(!towns.AccessQuestIds.Contains(608) && !towns.AccessQuestIds.Contains(609),"排除条件和进行中选择地图不反向当作解锁");
        var admission=definitions.Single(q=>q.Id==601) with {Grade="[side]",RewardType="[hell challenge]",Rewards=[new(0,1,null)]};
        That(!QuestQuickClearPlan.TryCreateManual(source with {CompletedQuests=[100,610]},Guid.NewGuid(),
            new(definitions.Select(q=>q.Id==601 ? admission : q).ToDictionary(q=>q.Id)),ImmutableDictionary<int,byte>.Empty,out _,out _,towns),
            "区域深渊专用奖励链不由主线跳过完成");
        var choices = new QuestCatalog(new() { [600]=Question(600) with {Grade="[side]"},[601]=Branch(601,600,1) with {Grade="[side]"} });
        That(!QuestQuickClearPlan.TryCreateManual(source,Guid.NewGuid(),choices,ImmutableDictionary<int,byte>.Empty,out _,out _,towns) &&
            !QuestQuickClearPlan.TryCreateManual(source,Guid.NewGuid(),choices,ImmutableDictionary<int,byte>.Empty.Add(600,1),out _,out _,towns),
            "独立区域支线问题不排进主线问答，也不能提交其回答");
    }

    // 仅测试资源容器：用一次读取回调安排锁外规划交错，不改变生产目录或增加时序等待。
    sealed class InterleavedRewards(IReadOnlyList<QuestRewardToken> values) : IReadOnlyList<QuestRewardToken>
    {
        public Action? BeforeRead { get; set; }
        public int Calls { get; private set; }
        void Observe()
        {
            var callback = BeforeRead;
            if (callback is null) return;
            BeforeRead = null; Calls++; callback();
        }
        public int Count { get { Observe(); return values.Count; } }
        public QuestRewardToken this[int index] { get { Observe(); return values[index]; } }
        public IEnumerator<QuestRewardToken> GetEnumerator() { Observe(); return values.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    static StoryQuest[] PlainDefinitions()
    {
        var first = QuestEligibilityTests.Quest(100) with
        {
            Condition="[seeking]",IntData=[10165055,50],Rewards=[new(0,10403,null),new(0,1,null)],
            DeclaredGoldMultiple=50,Definition=new([]) { Job="[all]",AcceptanceRewards=[new(0,10165055,null),new(0,3,null)] }
        };
        return [first,QuestEligibilityTests.Quest(101) with { MinimumLevel=19,Definition=new([]) { PrerequisiteGroups=[[100]] } },
            QuestEligibilityTests.Quest(102) with { MinimumLevel=20 },QuestEligibilityTests.Quest(103) with { Grade="[side]" },
            QuestEligibilityTests.Quest(105) with { Definition=new([]) { Collisions=[100] } },
            QuestEligibilityTests.Quest(110) with { Condition="[quest clear]",SubType=2,IntData=[111],MinimumLevel=18 },
            QuestEligibilityTests.Quest(111) with { MainQuest=110,Grade="[sub]",RewardType=null,Attributes=["not give exp quest"],MinimumLevel=18 },
            QuestEligibilityTests.Quest(300) with { Grade="[side]",Condition="[clear quest]",IntData=[101] }];
    }
    static StoryQuest[] QuestionDefinitions(bool chained)
    {
        var one = Question(100);
        var two = Question(200) with { Definition = Question(200).Definition! with
            { RequiredAnswers = chained ? [new(100,0)] : [] } };
        return [one,two,Branch(101,100,0),Branch(102,100,1),Branch(201,200,0),Branch(202,200,1)];
    }
    static StoryQuest Question(int id) => QuestEligibilityTests.Quest(id) with
    {
        Condition="[question]",Definition=new([new("question info",
            [new(6,0,"标题"),new(6,0,"正文"),new(6,0,"回答一"),new(6,0,"回答二")],[])])
    };
    static StoryQuest Branch(int id,int question,int answer) => QuestEligibilityTests.Quest(id) with
        { Definition=new([]) { RequiredAnswers=[new(question,answer)] } };
    static ActiveMainQuest Active(byte slot,ushort id,uint scalar=1) => new(slot,id,scalar) { AcceptanceId=Guid.NewGuid() };
    static string Assets(Character character) => JsonSerializer.Serialize(new
        { character.Level,character.Job,character.GrowType,character.EquipmentSlotFlags,character.AcceptedStoryLevel,
            character.Inventory,character.Skills,character.Learning,character.Position,character.Return,character.DungeonState });
    static Packet Answers(params QuestSkipAnswer[] answers)
    {
        var body = new byte[1+answers.Length*8]; body[0]=checked((byte)answers.Length);
        for (int index=0;index<answers.Length;index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1+index*8),answers[index].QuestId);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5+index*8),answers[index].Answer);
        }
        return new(1,1431,body);
    }
    static QuestConnectionCatalog Connections()
    {
        string[] strings = ["[connect quest unit]","[type]","episode quest","[parent key]","[name]","fixture",
            "[question quest]","[quick clear]","[sequence]","[/sequence]","[/connect quest unit]"];
        PvfToken[] tokens = [new(3,0),new(3,1),new(6,2),new(3,3),new(0,1007),new(3,4),new(6,5),new(3,6),new(0,400),
            new(3,7),new(0,999),new(0,1),new(0,500),new(0,500),new(3,8),new(0,400),new(0,0),new(0,500),new(0,0),new(3,9),new(3,10)];
        return QuestConnectionCatalog.Parse(tokens,index => strings[index]);
    }
    static GameConnectionContext Context(QuestOrdinaryCompletionTests.Fixture fixture,Stream output,GameConnectionSender sender,CancellationTokenSource stop) =>
        new(new(89,IPAddress.Loopback,output,stop),fixture.Owner,fixture.Session,sender);
    static long Scalar(QuestOrdinaryCompletionTests.Fixture fixture,string sql)
    {
        using var db=fixture.Open(); using var command=db.CreateCommand(); command.CommandText=sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    static async Task RejectAsync<T>(Func<Task> action,string message) where T:Exception
    {
        try { await action(); }
        catch (T) { That(true,message); return; }
        throw new InvalidOperationException("FAIL: expected "+typeof(T).Name+": "+message);
    }
    sealed class ControlledOutput : MemoryStream
    {
        int writes;
        public int FailOn { get; init; }
        public List<byte[]> Frames { get; } = [];
        public IEnumerable<ushort> Opcodes => Frames.Select(frame => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)
        {
            if (++writes == FailOn) throw new IOException("手册提交后发布故障");
            Frames.Add(buffer.ToArray()); return ValueTask.CompletedTask;
        }
    }
}
