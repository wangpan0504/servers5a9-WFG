using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestQuickClearPlanTests
{
    public static void RunCore()
    {
        int checks = Count;
        var question = Question(100);
        var marker = QuestEligibilityTests.Quest(101) with { Grade = "[invisible]", IntData = [-1],
            Definition = new([new("synchronization account quest",[new(0,1,null)],[])]) };
        var first = QuestEligibilityTests.Quest(200) with { MainQuest = 300 };
        var answered = Question(201);
        var inserted = QuestEligibilityTests.Quest(6381);
        var last = QuestEligibilityTests.Quest(202) with { Definition = new([TitleNotification("accept"),
            new("solve video",[new(6,0,"fixture.avi")],[]),new("casecade clear",[new(0,101,null)],[])]) };
        var parent = QuestEligibilityTests.Quest(300) with { Condition = "[quest clear]", SubType = 2, IntData = [200,301] };
        var child = QuestEligibilityTests.Quest(301) with { MainQuest = 300 };
        var state = QuestEligibilityTests.Quest(302) with { Condition = "[clear quest]", IntData = [202] };
        var definitions = new[] { question,marker,first,answered,inserted,last,parent,child,state,
            QuestEligibilityTests.Quest(205),QuestEligibilityTests.Quest(206),QuestEligibilityTests.Quest(400) };
        var quests = new QuestCatalog(definitions.ToDictionary(quest => quest.Id));
        int[] order = [100,205,200,201,6381,202,206];
        var quick = new QuestQuickClearDefinition(101,2,200,202);
        var connections = Connections(1007,100,quick,order);
        var activeQuestion = new ActiveMainQuest(0,100,QuestQuestionProgress.Unanswered) { AcceptanceId = Guid.NewGuid() };
        var source = new Character(1,0,"Quick",12,new(38,0,450,250),null)
        {
            GrowType = 17, EquipmentSlotFlags = 3, Inventory = new InventorySnapshot([],Gold: 1234),
            CompletedQuests = [201,900], QuestAnswers = ImmutableDictionary<int,byte>.Empty.Add(201,1).Add(900,2),
            AccountCompletedQuests = [901], AccountQuestAnswers = ImmutableDictionary<int,byte>.Empty.Add(901,1),
            ActiveMainQuests = [activeQuestion,Active(1,200),Active(2,300,2),Active(3,302),Active(4,400),Active(5,205),Active(6,206)]
        };
        That(QuestQuickClearPlan.TryCreate(source,1007,quests,connections,out var plan,out _),"任意资源 parent key 可规划，无固定任务或职业列表");
        That(ReferenceEquals(plan!.Expected,source) && plan.AcceptanceId == activeQuestion.AcceptanceId &&
            ReferenceEquals(plan.ActiveQuestion,activeQuestion),"计划绑定原角色、槽位和真实 question 接取身份");
        That(plan.Interval.Select(quest => quest.Id).SequenceEqual([200,201,6381,202]) &&
            plan.CompletedAfter.Order().SequenceEqual([100,101,200,201,202,900,6381]),"原序区间包含数字范围外插入任务及显式cascade标记，不扩展前后 sequence 或依赖图");
        That(plan.RemovedActive.Select(row => (int)row.QuestId).SequenceEqual([100,200]) &&
            plan.ActiveAfter.Select(row => (int)row.QuestId).SequenceEqual([300,302,400,205,206]),"只移除 question 与显式区间的活动槽");
        That(plan.ActiveAfter.Single(row => row.QuestId == 300).ConditionState == 1 &&
            plan.ActiveAfter.Single(row => row.QuestId == 302).ConditionState == 0 &&
            plan.ActiveAfter.Single(row => row.QuestId == 400) == source.ActiveMainQuests.Single(row => row.QuestId == 400),
            "重算其他顺序父任务和完成状态目标，保留无关活动任务及其身份");
        That(plan.Answer == 2 && plan.AnswersAfter[100] == 2 && plan.AnswersAfter[201] == 1 && plan.AnswersAfter[900] == 2 &&
            !plan.NewCompletedQuestIds.Contains(201),"保存零基跳过答案，既有普通完成项及其回答保持原值");
        That(plan.MarkerQuestId == 101 && ReferenceEquals(plan.MarkerDefinition,marker) && plan.CompletedAfter.Contains(101) &&
            plan.AccountCompletedAfter.SequenceEqual(source.AccountCompletedQuests) && ReferenceEquals(plan.AccountAnswersAfter,source.AccountQuestAnswers),
            "marker按已证级联消费者只加入本角色，不冒充普通账号任务");
        That(QuestQuickClearPlan.TryCreate(source with { AccountCompletedQuests = [901,6381] },1007,quests,connections,out var shared,out _) &&
            !shared!.NewCompletedQuestIds.Contains(6381) && !shared.CompletedAfter.Contains(6381) && shared.AccountCompletedAfter.Contains(6381),
            "已有账号完成项原样保留，不复制成新的角色完成记录");
        That(source.CompletedQuests.SequenceEqual([201,900]) && !source.QuestAnswers.ContainsKey(100) && source.ActiveMainQuests.Length == 7 &&
            source.Inventory.Gold == 1234 && source.GrowType == 17 && source.EquipmentSlotFlags == 3 && source.Level == 1,
            "纯规划不修改原任务数组、资产、等级、职业或槽位状态");
        for (byte job = 0; job < 16; job++)
            That(QuestQuickClearPlan.TryCreate(source with { Job = job },1007,quests,connections,out _,out _),"全部职业复用真实 question 资格 " + job);
        That(QuestQuickClearPlan.TryCreate(source with { ActiveMainQuests = source.ActiveMainQuests.Select(row =>
            row == activeQuestion ? row with { ConditionState = 0 } : row).ToArray() },1007,quests,connections,out _,out _),
            "尚未交付的普通选项不会被当作已完成分支，专用跳过入口仍可选择真实 skip 答案");
        That(TryReplace(last with { Definition = last.Definition! with { Fields = [TitleNotification("clear")] } },out _),
            "完成标题动画路径与五整数 fade 声明可保留为显示定义");
        That(TryReplace(last with { Definition = last.Definition! with { Fields = [TitleNotification("accept") with
            { Children = TitleNotification("accept").Children.Reverse().ToImmutableArray() }] } },out _),
            "标题显示子段顺序不改变无奖励规划");
        var visible = QuestEligibilityTests.Quest(202) with { Definition = new([VisibilityField(471,"[delete]"),VisibilityField(469,"[show]")])
        {
            NpcVisibility = [new([471],"[clear]","[delete]"),new([469],"[clear]","[show]")]
        } };
        That(QuestCoverage.HasOrdinaryDeclarations(visible) && QuestQuickClearPlan.TryCreate(source,1007,
            new QuestCatalog(definitions.Select(row => row.Id == 202 ? visible : row).ToDictionary(row => row.Id)),connections,
            out var displayed,out _) && ReferenceEquals(displayed!.Interval.Single(row => row.Id == 202).Definition,visible.Definition),
            "clear/delete复用已确认显示合同，无奖励规划保留原始NPC定义");
        foreach (var invalid in new[]
        {
            source with { ActiveMainQuests = source.ActiveMainQuests.Where(row => row != activeQuestion).ToArray() },
            source with { ActiveMainQuests = [activeQuestion with { AcceptanceId = Guid.Empty }] },
            source with { CompletedQuests = [100,201,900] },
            source with { QuestAnswers = source.QuestAnswers.Add(100,0) },
            source with { Job = 16 }
        })
            That(!QuestQuickClearPlan.TryCreate(invalid,1007,quests,connections,out _,out _),"未接取、无身份、普通已完成分支、持久答案冲突和失效角色均拒绝");
        var restricted = question with { MinimumLevel = 20 };
        That(!TryReplace(restricted,out string lowReason) && lowReason.Contains("等级不符",StringComparison.Ordinal),"当前等级资格沿用既有 active 交付合同");
        var targeted = question with { Definition = question.Definition! with { TargetCharacters = [new("[at priest]",1,1)] } };
        That(!TryReplace(targeted,out _) && QuestQuickClearPlan.TryCreate(source with { Job = 14 },1007,
            new QuestCatalog(definitions.Select(row => row.Id == 100 ? targeted : row).ToDictionary(row => row.Id)),connections,out _,out _),
            "目标职业与觉醒资格复用公共模型，不给跳过新增职业特例");
        foreach (var invalid in new[]
        {
            last with { Definition = last.Definition! with { AccountQuest = true } },
            last with { Definition = last.Definition! with { JobChange = 1 } },
            last with { Definition = last.Definition! with { AcceptanceRewards = [new(0,1,null),new(0,1,null)] } },
            last with { Condition = "[seeking]", IntData = [1,1] },
            last with { RewardType = "[skill point]" },
            last with { Definition = last.Definition! with { Fields = [new("future domain rule",[],[])] } },
            last with { Definition = last.Definition! with { Fields = [new("casecade clear",[new(0,999,null)],[])] } },
            last with { Definition = last.Definition! with { Fields = [new("quest title noti",[new(0,1,null)],[])] } },
            last with { Definition = last.Definition! with { Fields = [new("quest title noti",[],[])] } },
            last with { Definition = last.Definition! with { Fields = [new("quest title noti",[],
                [TitleNotification("accept").Children[0],TitleNotification("clear").Children[1]])] } },
            last with { Definition = last.Definition! with { Fields = [new("quest title noti",[],
                [TitleNotification("accept").Children[0],new("accept ani fade option",[new(0,700,null)],[])])] } },
            last with { Definition = last.Definition! with { Fields = [new("quest title noti",[],
                [TitleNotification("accept").Children[0],new("future title rule",[],[])])] } },
            last with { Definition = last.Definition! with { NpcVisibility = [new([471],"[clear]","[future]")] } },
            last with { Definition = last.Definition! with { NpcVisibility = [new([471],"[accept]","[delete]")] } },
            last with { Definition = last.Definition! with { RelationQuests = [999] } },
            last with { Definition = last.Definition! with { Diagnostics = ["坏定义"] } },
            inserted with { Condition = "[question]", IntData = [1], Definition = Question(6381).Definition }
        })
            That(!TryReplace(invalid,out string pending) && pending.Length != 0,"相关未知定义或未具备附属业务给出具体限制原因");
        That(!QuestQuickClearPlan.TryCreate(source,1007,new QuestCatalog(definitions.Where(row => row.Id != 6381).ToDictionary(row => row.Id)),
            connections,out _,out _),"原序区间缺失合法任务引用不能忽略后继续跳过");
        foreach (var invalid in new[] { quick with { Answer = 3 },quick with { StartQuestId = 999 },quick with { StartQuestId = 202,EndQuestId = 200 } })
            That(!QuestQuickClearPlan.TryCreate(source,1007,quests,Connections(1007,100,invalid,order),out _,out _),"答案越界、端点缺失或反序均拒绝");
        That(!QuestQuickClearPlan.TryCreate(source,1007,quests,Connections(1007,100,quick,[.. order,200]),out _,out _) &&
            !QuestQuickClearPlan.TryCreate(source,uint.MaxValue,quests,connections,out _,out _),"重复原序引用和不对应资源的完整 u32 parent key 不截断");
        Console.WriteLine($"PASS {Count-checks} 无奖励 quick clear 原序范围、资格和纯规划断言");

        bool TryReplace(StoryQuest replacement,out string reason) => QuestQuickClearPlan.TryCreate(source,1007,
            new QuestCatalog(definitions.Select(row => row.Id == replacement.Id ? replacement : row).ToDictionary(row => row.Id)),
            connections,out _,out reason);
    }

    public static void Run(PvfArchive pvf,QuestCatalog quests)
    {
        int checks = Count;
        var connections = QuestConnectionCatalog.Load(pvf);
        var titleIds = new List<int>();
        foreach (int key in new[] { 19,21,23 })
        {
            That(connections.TryGet(key,out var unit),"匹配 PVF 的普通 NPC quick clear 关联单元: " + key);
            var question = quests.Get(unit.QuestionQuestId!.Value);
            var definition = question.Definition!;
            var required = definition.PrerequisiteGroups.FirstOrDefault(group => group.Length != 0);
            var completed = (required.IsDefault ? ImmutableArray<int>.Empty : required)
                .Concat(definition.RequiredAnswers.Select(row => row.QuestId)).Distinct().ToArray();
            var source = new Character(1,0,"ActualQuick",10,new(38,0,450,250),null)
            {
                Level = 95, CompletedQuests = completed,
                QuestAnswers = definition.RequiredAnswers.ToImmutableDictionary(row => row.QuestId,row => checked((byte)row.Answer)),
                ActiveMainQuests = [new(0,checked((ushort)question.Id),QuestQuestionProgress.Unanswered) { AcceptanceId = Guid.NewGuid() }]
            };
            That(QuestQuickClearPlan.TryCreate(source,(uint)key,quests,connections,out var plan,out string reason),"真实三条旅程可无奖励规划: " + key + ": " + reason);
            var quick = unit.QuickClear!.Value;
            That(plan!.Interval[0].Id == quick.StartQuestId && plan.Interval[^1].Id == quick.EndQuestId &&
                plan.Answer == quick.Answer && plan.CompletedAfter.Contains(question.Id) && plan.CompletedAfter.Contains(quick.MarkerQuestId) &&
                plan.AccountCompletedAfter.IsEmpty && plan.ActiveAfter.IsEmpty,"真实定义起止、零基答案与角色cascade marker保持合同: " + key);
            foreach (var quest in plan.Interval)
            foreach (var title in quest.Definition!.Fields.Where(field => field.Name == "quest title noti"))
            {
                string phase = quest.Id == 6335 ? "clear" : "accept";
                That(title.Values.IsEmpty && title.Children.Length == 2 &&
                    title.Children[0] is { Children.Length: 0, Values: [{ Type: 6, Text: not null }] } &&
                    title.Children[0].Name == phase + " ani path" &&
                    title.Children[1].Name == phase + " ani fade option" && title.Children[1].Children.IsEmpty &&
                    title.Children[1].Values.Length == 5 && title.Children[1].Values.All(value => value.Type == 0 && value.Text is null),
                    "匹配 PVF 的标题通知保留嵌套路径与五整数 fade 声明: " + quest.Id);
                titleIds.Add(quest.Id);
            }
            if (key == 23)
            {
                var sequence = plan.Interval.Select(quest => quest.Id).ToArray(); int at = Array.IndexOf(sequence,6342);
                That(at >= 0 && sequence[at+1] == 6381 && sequence[at+2] == 6343,
                    "真实魔界大战跳过范围包含 6342 与 6343 之间的 6381");
                var terminal = plan.Interval.Single(quest => quest.Id == 6352);
                var visibility = terminal.Definition!;
                That(ReferenceEquals(visibility,quests.Get(6352).Definition) && visibility.NpcVisibility.Length == 5 &&
                    visibility.NpcVisibility.All(row => row.Condition == "[clear]") &&
                    visibility.NpcVisibility.Where(row => row.Visibility == "[delete]").SelectMany(row => row.Npcs).SequenceEqual([471,472]) &&
                    visibility.NpcVisibility.Count(row => row.Visibility == "[show]") == 2 &&
                    visibility.NpcVisibility.Count(row => row.Visibility == "[hide]") == 1 &&
                    visibility.Fields.Count(field => field.Name == "npc visibility") == 5,
                    "真实6352的两个clear/delete及show/hide显示声明完整保留供完成投影消费");
            }
        }
        That(titleIds.Order().SequenceEqual([6200,6205,6208,6301,6305,6330,6335,6340]),
            "三条旅程的八个真实标题通知全部参与无奖励规划");
        Console.WriteLine($"PASS {Count-checks} 匹配 PVF 真实旅程无奖励 quick clear 规划断言");
    }

    static ActiveMainQuest Active(byte slot,ushort id,uint scalar = 1) => new(slot,id,scalar) { AcceptanceId = Guid.NewGuid() };

    static QuestField TitleNotification(string phase) => new("quest title noti",[],
        [new(phase + " ani path",[new(6,0,"UI/QuestTitle/fixture.ani")],[]),new(phase + " ani fade option",
            new[] { 700,3000,1500,0,200 }.Select(value => new QuestRewardToken(0,value,null)).ToImmutableArray(),[])]);

    static QuestField VisibilityField(int npc,string visibility) => new("npc visibility",[],
        [new("npc",[new(0,npc,null)],[]),new("condition",[new(6,0,"[clear]")],[]),new("visibility",[new(6,0,visibility)],[])]);

    static StoryQuest Question(int id) => QuestEligibilityTests.Quest(id) with { Condition = "[question]",
        Definition = new([new("question info",Enumerable.Range(0,5).Select(index => new QuestRewardToken(6,index,"text")).ToImmutableArray(),[])])
        { Job = "[all]" } };

    static QuestConnectionCatalog Connections(int key,int question,QuestQuickClearDefinition quick,int[] sequence)
    {
        string[] strings = ["[connect quest unit]","[type]","episode quest","[parent key]","[name]","fixture",
            "[question quest]","[quick clear]","[sequence]","[/sequence]","[/connect quest unit]"];
        var tokens = new List<PvfToken> { new(3,0),new(3,1),new(6,2),new(3,3),new(0,key),new(3,4),new(6,5),new(3,6),new(0,question),
            new(3,7),new(0,quick.MarkerQuestId),new(0,quick.Answer),new(0,quick.StartQuestId),new(0,quick.EndQuestId),new(3,8) };
        foreach (int id in sequence) { tokens.Add(new(0,id)); tokens.Add(new(0,0)); }
        tokens.Add(new(3,9)); tokens.Add(new(3,10));
        return QuestConnectionCatalog.Parse(tokens,index => strings[index]);
    }
}
