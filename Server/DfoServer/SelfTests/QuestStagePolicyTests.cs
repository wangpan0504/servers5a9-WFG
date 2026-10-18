using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestStagePolicyTests
{
    public static void RunResources(Testing.TcpTestResources resources)
    {
        int checks = Count;
        var quests = resources.Quests;
        var graph = QuestStageGraph.Build(quests);
        var growth = Testing.SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
        int[] opening = [3054,3145,3146,3147,3148,3157,4873,4875];
        int[] transfers = [6931,6956,6961,6977,6941,6966,6982,6951,6971,6996,6997,6936,6987,6992,6946,6999];
        foreach (byte job in Enumerable.Range(0,16).Select(value => (byte)value))
        {
            foreach (var edge in new (int Source,int Next,int Target)[] { (4875,3149,11),(3149,3150,13),(3150,3151,15),(3167,3168,19) })
            {
                int[] done = edge.Source == 3167
                    ? opening.Concat(Enumerable.Range(3149,19).Where(id=>id!=3154)).Append(6929).Append(transfers[job]).Distinct().ToArray()
                    : opening.Concat(edge.Source is 3149 or 3150 ? Enumerable.Range(3149,edge.Source-3148) : []).ToArray();
                byte grow = edge.Source == 3167 && job is not (9 or 10) ? (byte)1 : (byte)0;
                byte level = checked((byte)(edge.Target-1));
                var id = Guid.NewGuid();
                var before = new Character(1,0,"ResourceStage",1,new(38,2,450,240),null) {
                    Job=job,GrowType=grow,Level=level,CompletedQuests=done.Where(q=>q!=edge.Source).ToArray(),
                    ActiveMainQuests=[new(0,checked((ushort)edge.Source),0) {AcceptanceId=id}],
                    Learning=CharacterLearningState.Uninitialized with {Experience=growth.GetLevelStartExperience(level)} };
                var after = before with {Revision=2,CompletedQuests=done,ActiveMainQuests=[]};
                var plan = QuestStagePolicy.Prepare(before,after,quests.Get(edge.Source),id,quests,graph,growth.CumulativeExperience,new(1,1,[]));
                That(plan.Claim is { } claim && claim.TargetLevel == edge.Target &&
                    claim.AfterExperience == growth.GetLevelStartExperience(checked((byte)edge.Target)) &&
                    plan.WitnessesAfter.Single().BoundaryQuestIds.SequenceEqual(new[] {edge.Next}) &&
                    QuestEligibility.Evaluate(after,quests.Get(edge.Next),quests,mode:QuestEligibilityMode.IgnoreMinimumLevel).Eligible,
                    $"真实Catalog job{job}/grow{grow} {edge.Source}→{edge.Next}/T{edge.Target}唯一frontier：{plan.Reason}，候选={string.Join(",",plan.WitnessesAfter.SelectMany(w=>w.BoundaryQuestIds))}");
            }
            var completed = opening.Concat(Enumerable.Range(3149,5)).ToArray();
            var acceptance = Guid.NewGuid();
            var start = new Character(1,0,"TransferStage",1,new(38,2,450,240),null) {Job=job,Level=15,
                CompletedQuests=completed.Where(id=>id!=3153).ToArray(),ActiveMainQuests=[new(0,3153,0){AcceptanceId=acceptance}],
                Learning=CharacterLearningState.Uninitialized with {Experience=growth.GetLevelStartExperience(15)}};
            var end = start with {Revision=2,CompletedQuests=completed,ActiveMainQuests=[]};
            var transfer = QuestStagePolicy.Prepare(start,end,quests.Get(3153),acceptance,quests,graph,growth.CumulativeExperience,new(1,1,[]));
            That(QuestEligibility.Evaluate(end,quests.Get(6929),quests).Eligible &&
                QuestEligibility.Evaluate(end,quests.Get(transfers[job]),quests).Eligible && transfer.Claim is null && transfer.BonusExperience == 0,
                $"job{job} 3153后真实6929及本职业选择仍可做，不误补");
        }
        Console.WriteLine($"PASS {Count-checks} 真实Catalog四边×16职业及转职工作frontier断言（非实机）");
    }

    public static void RunCore()
    {
        int checks = Count;
        var a = QuestEligibilityTests.Quest(100);
        var c = QuestEligibilityTests.Quest(101) with { MinimumLevel = 11,
            Definition = new([]) { Job = "[all]", PrerequisiteGroups = [[100]] } };
        var n = QuestEligibilityTests.Quest(102) with { MinimumLevel = 13,
            Definition = new([]) { Job = "[all]", PrerequisiteGroups = [[100]] } };
        var quests = new QuestCatalog(new[] { a,c,n }.ToDictionary(q => q.Id));
        var graph = QuestStageGraph.Build(quests);
        var table = Enumerable.Range(0,100).Select(i => (ulong)i*1000).ToArray();
        var idA = Guid.NewGuid();
        var before = new Character(1,0,"Stage",1,new(38,2,450,240),null) {
            Level = 11, Learning = CharacterLearningState.Uninitialized with { Experience = 10000 },
            ActiveMainQuests = [new(0,100,0) { AcceptanceId = idA }] };
        var afterA = before with { Revision = 2, CompletedQuests = [100], ActiveMainQuests = [] };
        var deferred = QuestStagePolicy.Prepare(before,afterA,a,idA,quests,graph,table,new(1,1,[]));
        bool waits = deferred.Claim is null && deferred.WitnessesAfter is [{ Status: QuestStageStatus.Deferred, TargetLevel: 13 }];
        var idC = Guid.NewGuid();
        var beforeC = afterA with { ActiveMainQuests = [new(0,101,0) { AcceptanceId = idC }] };
        var afterC = beforeC with { Revision = 3, CompletedQuests = [100,101], ActiveMainQuests = [] };
        // 首轮无实现时也独立检验续判行为，避免首个断言短路掩盖第二个反例。
        var pending = waits ? deferred.WitnessesAfter : [new QuestStageWitness(idA,100,2,13,[101,102],
            [new(100,101,[100]),new(100,102,[100])],1,QuestStageStatus.Deferred)];
        var grant = QuestStagePolicy.Prepare(beforeC,afterC,c,idC,quests,graph,table,new(1,2,pending));
        bool resumes = grant.Claim is { Amount: 2000, TargetLevel: 13 } &&
            grant.Claim.SourceAcceptanceId == idA && grant.Claim.TriggerAcceptanceId == idC;
        That(waits && resumes, $"同级C冻结A13延期={waits}；C交付沿原A补13={resumes}");
        That(deferred.Expected == new QuestStageState(1,1,[]) && deferred.BonusExperience == 0 &&
            grant.WitnessesAfter.Single().Status == QuestStageStatus.Granted,
            "纯计划保留期望版本，仅续判的来源变为Granted");

        QuestStagePlan Finish(StoryQuest[] definitions, Character? start = null, Character? end = null,
            QuestStageState? state = null, ulong[]? experience = null)
        {
            var catalog = new QuestCatalog(definitions.ToDictionary(q => q.Id));
            return QuestStagePolicy.Prepare(start ?? before, end ?? afterA, definitions[0], idA,
                catalog, QuestStageGraph.Build(catalog), experience ?? table, state ?? new(1,1,[]));
        }
        var direct = Finish([a,n]);
        That(direct.Claim is { TargetLevel:13, Amount:2000, BeforeExperience:10000, AfterExperience:12000 },
            "直接后继只补累计表目标起点");
        var richAfter = afterA with { Level=13, Learning=afterA.Learning with { Experience=12500 } };
        var satisfied = Finish([a,n], end:richAfter);
        That(satisfied.Claim is null && satisfied.WitnessesAfter is [{TargetLevel:13,Status:QuestStageStatus.Satisfied}],
            "普通奖励自然越过目标也以Satisfied0消费来源和目标");
        That(Finish([a,n],state:new(1,1,satisfied.WitnessesAfter)).Claim is null,
            "Satisfied后降级不能再次利用同来源");
        var d = QuestEligibilityTests.Quest(103) with { MinimumLevel=12,
            Definition=new([]) { PrerequisiteGroups=[[101]],Job="[all]" } };
        var dCatalog = new QuestCatalog(new[] { a,c,n,d }.ToDictionary(q => q.Id));
        var earlier = QuestStagePolicy.Prepare(beforeC,afterC,c,idC,dCatalog,QuestStageGraph.Build(dCatalog),table,new(1,2,deferred.WitnessesAfter));
        That(earlier.Claim is { TargetLevel:12,Amount:1000 } && earlier.Claim.SourceAcceptanceId==idC &&
            earlier.WitnessesAfter.Single(row=>row.SourceAcceptanceId==idA) is {TargetLevel:13,Status:QuestStageStatus.Deferred},
            "C新开D12先补12，A13目标固定且本次不递归");
        var afterEarlier = afterC with { Level=12, Learning=afterC.Learning with { Experience=11000 },
            ActiveMainQuests=[new(0,103,0) {AcceptanceId=Guid.NewGuid()}] };
        var idD = afterEarlier.ActiveMainQuests[0].AcceptanceId;
        var finishD = afterEarlier with { Revision=4,CompletedQuests=[100,101,103],ActiveMainQuests=[] };
        var finalA = QuestStagePolicy.Prepare(afterEarlier,finishD,d,idD,dCatalog,QuestStageGraph.Build(dCatalog),table,new(1,3,earlier.WitnessesAfter));
        That(finalA.Claim is {TargetLevel:13,Amount:1000} && finalA.Claim.SourceAcceptanceId==idA,
            "较早D真实完成后可沿持久A证据继续13");

        That(Finish([a,c,n],end:afterA with { ActiveMainQuests=[new(1,101,0) { AcceptanceId=Guid.NewGuid() }] })
            .WitnessesAfter is [{Status:QuestStageStatus.Deferred}], "条件0未交付活动主线仍为工作");
        var and = n with {Definition=new([]) {Job="[all]",PrerequisiteGroups=[[100,104]]}};
        var b=QuestEligibilityTests.Quest(104);
        That(Finish([a,and,b]).Claim is null, "AND前置未齐不补足");
        That(Finish([a,and,b],start:before with {CompletedQuests=[104]},end:afterA with {CompletedQuests=[100,104]})
            .Claim is {TargetLevel:13}, "已有合法AND成员加本次真实收尾允许直接候选");
        var missingAnd=Finish([a,and],start:before with {CompletedQuests=[104]},end:afterA with {CompletedQuests=[100,104]});
        That(missingAnd.Claim is null && missingAnd.WitnessesAfter.IsEmpty && missingAnd.Reason.Contains("unknown"),
            "AND旧完成位不能替代缺失定义");
        var or = n with {Definition=new([]) {Job="[all]",PrerequisiteGroups=[[3154],[100]]}};
        That(Finish([a,or]).Claim is {TargetLevel:13}, "有效OR组旁缺3154不误挡");
        foreach(var blocked in new[]
        {
            n with { Definition=n.Definition! with {Job="[fighter]"} },
            n with { Definition=n.Definition! with {IsEvent=true} },
            n with { Definition=n.Definition! with {Collisions=[100]} },
            n with { MaximumLevel=12 },
            n with { MainQuest=100 }, n with { Grade="[daily]" },
            n with { Definition=n.Definition! with {AccountQuest=true} }
        })
            That(Finish([a,blocked]).Claim is null,"后继非最低等级资格不放宽");
        var answer=n with {Definition=n.Definition! with {RequiredAnswers=[new(100,1)]}};
        That(Finish([a,answer],end:afterA with {QuestAnswers=new Dictionary<int,byte>{{100,1}}.ToImmutableDictionary()})
            .Claim is {TargetLevel:13}, "普通回答快照用于阶段资格");
        That(Finish([a,answer],end:afterA with {QuestAnswers=new Dictionary<int,byte>{{100,2}}.ToImmutableDictionary()})
            .Claim is null, "未选择的回答分支不补足");
        var noAnswer=Finish([a,answer]);
        That(noAnswer.Claim is null && noAnswer.Reason.Contains("unknown"),"缺失回答仅诊断未知阶段");
        var closedEvent=c with {Definition=c.Definition! with {IsEvent=true,PrerequisiteGroups=[[100,3154]]}};
        That(Finish([a,n,closedEvent]).Claim is {TargetLevel:13},"未启用活动分支不阻挡普通主线阶段");
        var unknown=c with {Condition="[unknown]"};
        var unknownStage=Finish([a,n,unknown]);
        That(unknownStage.Claim is null && unknownStage.Reason.Contains("unknown"),"相关普通主线未知目标不被忽略后越过");
        var transferredNext=n with {Definition=n.Definition! with {TargetCharacters=[new("[swordman]",1,0)]}};
        That(Finish([a,transferredNext],end:afterA with {GrowType=1}).Claim is {TargetLevel:13},
            "后继资格使用普通完成后职业快照");
        for(byte job=0;job<16;job++)
            That(Finish([a,n],start:before with {Job=job},end:afterA with {Job=job}).Claim is {TargetLevel:13,Amount:2000},
                "16职业共享规则，不以样本职业限制阶段 " + job);

        // 完成节点仅是因果图穿行证据，不能再以角色现职业要求重新否定历史完成。
        var old=c with {Definition=c.Definition! with {Job="[fighter]"}};
        var through=n with {Definition=n.Definition! with {PrerequisiteGroups=[[101]]}};
        That(Finish([a,old,through],start:before with {CompletedQuests=[101]},end:afterA with {CompletedQuests=[100,101]})
            .Claim is {TargetLevel:13}, "转职后穿行已完成的同因果节点");
        var blockedPath=Finish([a,c,through]);
        That(blockedPath.Claim is null && blockedPath.WitnessesAfter.IsEmpty,
            "每支首未完成停止，不跨C寻找远处N13");
        var cycle=a with {Definition=a.Definition! with {PrerequisiteGroups=[[101]]}};
        var cyclic=Finish([cycle,c,n],start:before with {CompletedQuests=[101]},end:afterA with {CompletedQuests=[100,101]});
        That(cyclic.Claim is null && cyclic.Reason.Contains("unknown"),"相关完成因果环保守诊断");

        foreach(var excluded in new[]
        {
            a with {MainQuest=104}, a with {Attributes=["not give exp quest"]},
            a with {Definition=a.Definition! with {AccountQuest=true}},
            a with {Grade="[normaly repeat]"}, a with {Grade="[daily]"}, a with {Grade="[side]"},
            a with {Condition="[unknown]"}
        })
            That(Finish([excluded,n]).WitnessesAfter.IsEmpty,"子任务/禁经验/账号/周期/非主线源不触发");
        That(Finish([a,n],start:before with {ActiveMainQuests=[]}).WitnessesAfter.IsEmpty &&
            Finish([a,n],start:before with {CompletedQuests=[100]}).WitnessesAfter.IsEmpty,
            "历史完成位与无真实AcceptanceId不能签发来源");
        That(Finish([a]).WitnessesAfter.IsEmpty,"无后继没有阶段补足");
        var at95=n with {MinimumLevel=95};
        That(Finish([a,at95]).Claim is {TargetLevel:95,Amount:84000},"95级目标允许累计表补差");
        That(Finish([a,n with {MinimumLevel=96}]).Claim is null &&
            Finish([a,n],start:before with {Level=95},end:afterA with {Level=95}).Claim is null,
            "超95目标与满级不生成阶段奖励");
        var huge=table.ToArray(); huge[12]=(ulong)int.MaxValue+10001;
        var overflow=Finish([a,n],experience:huge);
        That(overflow.Claim is null && overflow.WitnessesAfter.IsEmpty && overflow.Reason.Contains("unknown"),
            "超int经验差在冻结前诊断不给额外经验");
        var duplicate=deferred.WitnessesAfter.Single() with {SourceAcceptanceId=Guid.NewGuid(),SourceQuestId=105};
        var other=a with {Id=105};
        var twoCatalog=new QuestCatalog(new[] {a,c,n with {Definition=n.Definition! with {PrerequisiteGroups=[[100],[105]]}},other}.ToDictionary(q=>q.Id));
        var two=QuestStagePolicy.Prepare(beforeC with {CompletedQuests=[100,105]},afterC with {CompletedQuests=[100,101,105]},c,idC,
            twoCatalog,QuestStageGraph.Build(twoCatalog),table,new(1,2,[deferred.WitnessesAfter.Single(),duplicate with {
                BoundaryQuestIds=[102],Evidence=[new(105,102,[105])] }]));
        That(two.Claim is {TargetLevel:13,Amount:2000} && two.WitnessesAfter.Count(row=>row.Status==QuestStageStatus.Granted)==1 &&
            two.WitnessesAfter.Count(row=>row.Status==QuestStageStatus.Satisfied)==1,"多个pending同T仅发一次且其余同次闭合");
        var mismatch=Finish([a,n],state:new(2,1,[]));
        That(mismatch.Claim is null && mismatch.Reason.Contains("unknown"),"状态角色或revision不符不建立阶段资格");
        var cascade = a with {Definition=a.Definition! with { Fields=[new("casecade clear",[new(0,106,null)],[])] }};
        var marker=QuestEligibilityTests.Quest(106) with {Definition=new([]) {
            Fields=[new("synchronization account quest",[new(0,1,null)],[])] }};
        var viaMarker=n with {Definition=n.Definition! with {PrerequisiteGroups=[[106]]}};
        That(Finish([cascade,marker,viaMarker],end:afterA with {CompletedQuests=[100,106],AccountCompletedQuests=[106]})
            .Claim is {TargetLevel:13,Amount:2000},"真实普通源可使用资源确认cascade新完成事实");
        That(Finish([marker,viaMarker],start:before with {ActiveMainQuests=[new(0,106,0){AcceptanceId=idA}]},
            end:afterA with {CompletedQuests=[106]}).Claim is null,"独立cascade marker不能签发阶段来源");
        var question=a with {Condition="[question]",Definition=a.Definition! with {
            Fields=[new("question info",[new(6,0,"标题"),new(6,0,"提示"),new(6,0,"选项0"),new(6,0,"选项1")],[])] }};
        That(Finish([question,answer],start:before with {ActiveMainQuests=[new(0,100,1){AcceptanceId=idA}]},
            end:afterA with {QuestAnswers=ImmutableDictionary<int,byte>.Empty.Add(100,1)})
            .Claim is {TargetLevel:13},"问题任务真实非零答案来源可触发阶段");
        var silentC=c with {Attributes=["not give exp quest"]};
        var silentCatalog=new QuestCatalog(new[] {a,silentC,n}.ToDictionary(q=>q.Id));
        var silentPending=QuestStagePolicy.Prepare(before,afterA,a,idA,silentCatalog,QuestStageGraph.Build(silentCatalog),table,new(1,1,[]));
        That(silentPending.WitnessesAfter is [{Status:QuestStageStatus.Deferred,TargetLevel:13}],
            "禁经验普通后继仍是当前工作，不得越过");
        var silentGrant=QuestStagePolicy.Prepare(beforeC,afterC,silentC,idC,silentCatalog,QuestStageGraph.Build(silentCatalog),table,new(1,2,silentPending.WitnessesAfter));
        That(silentGrant.Claim is {TargetLevel:13,Amount:2000} && silentGrant.Claim.SourceAcceptanceId==idA &&
            silentGrant.Claim.TriggerAcceptanceId==idC && silentGrant.WitnessesAfter.Length==1,
            "禁经验C仅续判既有A来源，不给C新资格");
        var silentD=new QuestCatalog(new[] {a,silentC,n,d}.ToDictionary(q=>q.Id));
        var silentEarlier=QuestStagePolicy.Prepare(beforeC,afterC,silentC,idC,silentD,QuestStageGraph.Build(silentD),table,new(1,2,silentPending.WitnessesAfter));
        That(silentEarlier.Claim is null && silentEarlier.WitnessesAfter is [{Status:QuestStageStatus.Deferred,TargetLevel:13}],
            "禁经验C开D12只挡A13，不给C创12来源");

        var excludedFighter=c with {Definition=c.Definition! with {
            TargetCharacters=[new("[fighter]",-1,-1)],PrerequisiteGroups=[[100,3154]]}};
        var knownExcluded=Finish([a,n,excludedFighter]);
        var unansweredQuestion=question with {Id=104};
        var undecided=c with {Definition=c.Definition! with {RequiredAnswers=[new(104,1)]}};
        var unresolvedAnswer=Finish([a,n,undecided,unansweredQuestion]);
        bool excludesKnown=knownExcluded.Claim is {TargetLevel:13,Amount:2000};
        bool preservesUnknown=unresolvedAnswer.Claim is null && unresolvedAnswer.WitnessesAfter.IsEmpty &&
            unresolvedAnswer.Reason=="stage-unknown-answer";
        That(excludesKnown && preservesUnknown,
            $"已知fighter排除分支不被缺AND误挡={excludesKnown}({knownExcluded.Reason})；未完成Q104回答未知不提前补足={preservesUnknown}({unresolvedAnswer.Reason})");
        foreach(var inapplicable in new[]
        {
            excludedFighter,
            excludedFighter with {Definition=excludedFighter.Definition! with {
                TargetCharacters=[],NonTargetCharacters=[new("[swordman]",-1,-1)]}},
            excludedFighter with {Definition=excludedFighter.Definition! with {TargetCharacters=[],GrowType=1}},
            excludedFighter with {Definition=excludedFighter.Definition! with {TargetCharacters=[],JobChange=2}},
            excludedFighter with {Definition=excludedFighter.Definition! with {TargetCharacters=[],JobChange=3}}
        })
            That(Finish([a,n,inapplicable]).Claim is {TargetLevel:13,Amount:2000},
                "明确不适用的职业/转职/觉醒分支先于未齐AND排除");
        That(Finish([a,n,undecided,unansweredQuestion],end:afterA with {CompletedQuests=[100,104],
            QuestAnswers=ImmutableDictionary<int,byte>.Empty.Add(104,0)}).Claim is {TargetLevel:13},
            "已有合法其它回答可以排除当前分支");
        var chosenOther=undecided with {Definition=undecided.Definition! with {PrerequisiteGroups=[[100,3154]]}};
        That(Finish([a,n,chosenOther,unansweredQuestion],end:afterA with {CompletedQuests=[100,104],
            QuestAnswers=ImmutableDictionary<int,byte>.Empty.Add(104,0)}).Claim is {TargetLevel:13},
            "已选其它回答分支不因该分支缺AND而误挡");

        var oldMarkerBefore=before with {CompletedQuests=[106]};
        var oldMarkerAfter=afterA with {CompletedQuests=[100,106]};
        That(Finish([cascade,marker,viaMarker],start:oldMarkerBefore,end:oldMarkerAfter).Claim is {TargetLevel:13,Amount:2000},
            "资源关联的旧cascade marker可供本次真实A穿行");
        var unlinkedMarker=Finish([a,marker,viaMarker],start:oldMarkerBefore,end:oldMarkerAfter);
        That(unlinkedMarker.Claim is null && unlinkedMarker.WitnessesAfter.IsEmpty,
            "没有资源关联的旧marker不能挂到A路径");
        That(Finish([cascade,marker,viaMarker],start:before with {CompletedQuests=[100,106],ActiveMainQuests=[]},
            end:oldMarkerAfter).WitnessesAfter.IsEmpty,"旧A与marker没有本次真实A完成则不创来源");
        var otherOr=undecided with {Definition=undecided.Definition! with {PrerequisiteGroups=[[100,105],[106]]}};
        var unprovenBranch=Finish([a,n,otherOr,unansweredQuestion,QuestEligibilityTests.Quest(105),QuestEligibilityTests.Quest(106)],
            start:before with {CompletedQuests=[106]},end:afterA with {CompletedQuests=[100,106]});
        That(unprovenBranch.Claim is {TargetLevel:13,Amount:2000} && unprovenBranch.Claim.SourceAcceptanceId==idA,
            $"当前A侧AND未齐且另一OR已齐时，不以无因果证明分支的未知答案阻挡N13({unprovenBranch.Reason})");
        Console.WriteLine($"PASS {Count - checks} 主线阶段策略断言");
    }
}
