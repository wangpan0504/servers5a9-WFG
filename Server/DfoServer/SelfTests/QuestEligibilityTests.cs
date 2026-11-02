using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestEligibilityTests
{
    public static void Run()
    {
        int checks = Count;
        var origin = new Character(1, 0, "Quest", 1, new Location(38, 2, 450, 240), null);
        var first = Quest(100);
        var next = Quest(101) with { Definition = new QuestDefinition([]) { PrerequisiteGroups = [[100, 102], [103]] } };
        var catalog = new QuestCatalog(new[] { first, next, Quest(102), Quest(103) }.ToDictionary(quest => quest.Id));
        That(QuestEligibility.Evaluate(origin, first, catalog).Visible, "普通资格允许Lv1未完成任务");
        var congratulations = first with { Grade = "[common unique]", SubType = -1,
            Definition = new([]) { JobChange = 1, TargetCharacters = [new("[knight]", 1, -1),new("[knight]", 2, -1)] } };
        foreach (byte grow in new byte[] { 1, 2, 17, 18, 33, 34 })
            That(QuestEligibility.Evaluate(origin with { Job = 12, GrowType = grow }, congratulations, catalog).Visible &&
                QuestStateProgress.Type(congratulations) is null,
                "普通转职祝贺允许匹配已转职及觉醒角色，但保留实际NPC会面目标");
        foreach (var rejected in new[] { origin with { Job = 12 }, origin with { Job = 0, GrowType = 1 },
            origin with { Job = 12, GrowType = 3 } })
            That(!QuestEligibility.Evaluate(rejected, congratulations, catalog).Eligible,
                "转职祝贺仍拒绝未转职、其它职业和不匹配分支");
        That(!QuestEligibility.TransferredAutoMeet(congratulations with { SubType = 0 }) &&
            !QuestEligibility.TransferredAutoMeet(congratulations with { RewardType = "[grow type]" }),
            "未知会面子类型及真实转职奖励不使用祝贺资格");
        var levelGate = first with { MinimumLevel = 13 };
        That(!QuestEligibility.Evaluate(origin, levelGate, catalog).Eligible &&
            QuestEligibility.Evaluate(origin, levelGate, catalog, mode: QuestEligibilityMode.IgnoreMinimumLevel).Eligible,
            "阶段模式仅忽略最低等级");
        foreach (var guarded in new[]
        {
            levelGate with { MaximumLevel = 12 },
            levelGate with { Definition = new([]) { Job = "[fighter]" } },
            levelGate with { Definition = new([]) { IsEvent = true } },
            levelGate with { Definition = new([]) { RequiredAnswers = [new(103,1)] } },
            levelGate with { Definition = new([]) { PrerequisiteGroups = [[103]] } },
            levelGate with { Definition = new([]) { Collisions = [103] } }
        })
            That(!QuestEligibility.Evaluate(origin with { ActiveMainQuests = [new(0,103,0)] }, guarded, catalog,
                mode: QuestEligibilityMode.IgnoreMinimumLevel).Eligible, "阶段模式保留非最低等级资格");
        That(!QuestEligibility.Evaluate(origin with { Level = 14 }, levelGate with { MaximumLevel = 13 }, catalog,
            mode: QuestEligibilityMode.IgnoreMinimumLevel).Eligible, "阶段模式不忽略最高等级");
        That(!QuestEligibility.Evaluate(origin, next, catalog).Eligible, "前置组不以任意单个前置代替整组");
        That(!QuestEligibility.Evaluate(origin with { CompletedQuests = [100] }, next, catalog).Eligible,
            "AND组缺一个前置仍拒绝");
        That(QuestEligibility.Evaluate(origin with { CompletedQuests = [100, 102] }, next, catalog).Eligible &&
            QuestEligibility.Evaluate(origin with { CompletedQuests = [103] }, next, catalog).Eligible, "前置组内AND、组间OR");
        for (byte job = 0; job < 16; job++)
            That(QuestEligibility.Evaluate(origin with { Job = job }, first, catalog).Eligible, "all职业资格 " + job);
        That(!QuestEligibility.Evaluate(origin, first with { MinimumLevel = 9999 }, catalog).Eligible &&
            !QuestEligibility.Evaluate(origin, first with { MinimumLevel = -1 }, catalog).Eligible, "特殊等级不截为普通等级");
        var target = first with { Definition = new QuestDefinition([]) { TargetCharacters = [new("[at priest]", 2, 1)] } };
        That(QuestEligibility.Evaluate(origin with { Job = 14, GrowType = 18 }, target, catalog).Eligible &&
            !QuestEligibility.Evaluate(origin with { Job = 14, GrowType = 2 }, target, catalog).Eligible &&
            !QuestEligibility.Evaluate(origin with { Job = 0, GrowType = 18 }, target, catalog).Eligible,
            "目标职业三元组同时限制职业、转职与觉醒");
        var conflict = first with { Definition = new QuestDefinition([]) { Collisions = [101] } };
        That(!QuestEligibility.Evaluate(origin with { ActiveMainQuests = [new(0, 101, 1)] }, conflict, catalog).Eligible &&
            !QuestEligibility.Evaluate(origin with { CompletedQuests = [101] }, conflict, catalog).Eligible, "冲突检查覆盖接取与完成");
        That(QuestEligibility.Evaluate(origin, first with { Definition = new QuestDefinition([]) { AccountQuest = true } }, catalog).Eligible &&
            !QuestEligibility.Evaluate(origin with { AccountCompletedQuests = [100] }, first, catalog).Eligible &&
            !QuestEligibility.Evaluate(origin, first with { Definition = new QuestDefinition([]) { RequiredAnswers = [new(103, 1)] } }, catalog).Eligible,
            "账号未完成允许普通资格，账号已完成阻止重复；未实现回答不能按答案0放行");
        That(QuestEligibility.Evaluate(origin with { AccountCompletedQuests = [103] },next,catalog).Eligible &&
            !QuestEligibility.Evaluate(origin with { AccountCompletedQuests = [103] },first with { Definition = new([]) { AccountCollisions = [103] } },catalog).Eligible,
            "账号完成位参与前置组与账号冲突判断");
        var parent = Quest(200) with { Condition = "[quest clear]", SubType = 2, IntData = new[] { 202, 201 } };
        var child1 = Quest(201) with { MainQuest = 200 };
        var child2 = Quest(202) with { MainQuest = 200 };
        catalog = new QuestCatalog(new[] { parent, child1, child2 }.ToDictionary(quest => quest.Id));
        var inParent = origin with { ActiveMainQuests = [new(0, 200, 2)] };
        That(!QuestEligibility.Evaluate(origin, child2, catalog).Eligible &&
            QuestEligibility.Evaluate(inParent, child2, catalog) is { Eligible: true, Visible: false } &&
            !QuestEligibility.Evaluate(inParent, child1, catalog).Eligible, "隐藏子任务按资源顺序等待父任务真实入口");
        That(QuestEligibility.Evaluate(inParent with { CompletedQuests = [202] }, child1, catalog).Eligible,
            "完成首个子任务后才开放后一个");
        var sequential = parent with { SubType = 2 };
        var related = new QuestCatalog(new[] { sequential, child1, child2 }.ToDictionary(quest => quest.Id));
        That(QuestRelationProgress.DialogueChild(child2, related), "确认的顺序父任务允许城镇子任务入口");
        var zeroItemChild = child2 with { Attributes = ["not give exp quest"],
            Rewards = [new(0,0,null),new(0,0,null)] };
        That(QuestRelationProgress.ZeroRewardChild(zeroItemChild) &&
            QuestRelationProgress.ZeroRewardChild(zeroItemChild with { RewardType = null, Rewards = [] }),
            "禁经验子任务兼容明确0/0物品占位与旧空奖励声明");
        foreach (var rewarded in new[]
        {
            zeroItemChild with { Attributes = [] }, zeroItemChild with { MainQuest = null },
            zeroItemChild with { Rewards = [new(0,2058,null),new(0,1,null)] },
            zeroItemChild with { Rewards = [new(0,0,null)] }, zeroItemChild with { RewardType = "[tp]" },
            zeroItemChild with { DeclaredGoldMultiple = 1 },
            zeroItemChild with { Definition = zeroItemChild.Definition! with { SelectRewards = [new(0,2058,null),new(0,1,null)] } }
        })
            That(!QuestRelationProgress.ZeroRewardChild(rewarded),"实际经验、奖励或不完整声明不能自动交付子任务");
        var orphanCatalog = new QuestCatalog(new[] { sequential,child1,zeroItemChild }.ToDictionary(quest => quest.Id));
        var orphan = origin with { CompletedQuests = [200], ActiveMainQuests = [new(0,202,0) { AcceptanceId = Guid.NewGuid() }] };
        That(QuestRelationProgress.CompletedParentChild(orphan,zeroItemChild,orphanCatalog) == 200,
            "已完成父任务和持久终末子接取允许零奖励归档");
        foreach (var invalid in new[] { orphan with { CompletedQuests = [] },
            orphan with { CompletedQuests = [], AccountCompletedQuests = [200] },
            orphan with { ActiveMainQuests = [orphan.ActiveMainQuests[0] with { ConditionState = 1 }] },
            orphan with { ActiveMainQuests = [orphan.ActiveMainQuests[0] with { AcceptanceId = Guid.Empty }] },
            orphan with { ActiveMainQuests = [orphan.ActiveMainQuests[0],new(1,200,0) { AcceptanceId = Guid.NewGuid() }] } })
            That(QuestRelationProgress.CompletedParentChild(invalid,zeroItemChild,orphanCatalog) is null,
                "未完成父任务、账号记录、未达成子任务及仍在进行的父任务不能走历史归档");
        That(QuestRelationProgress.CompletedParentChild(orphan,zeroItemChild with { MainQuest = 199 },orphanCatalog) is null &&
            QuestRelationProgress.CompletedParentChild(orphan,zeroItemChild with { Rewards = [new(0,2058,null),new(0,1,null)] },orphanCatalog) is null,
            "无真实反向归属或有奖励的子任务不能归档");
        That(QuestRelationProgress.Initial(sequential,related,[]) == 2 &&
            QuestRelationProgress.Initial(sequential,related,[202]) == 1 &&
            QuestRelationProgress.Initial(sequential,related,[201,202]) == 0,
            "新父任务按实际未完成子项初始化；已有完成记录不被重置或伪造");
        foreach (var unsupported in new[]
        {
            sequential with { SubType = 0 }, sequential with { IntData = [202,202] },
            sequential with { IntData = [202,29999] }, sequential with { Grade = "[daily]" },
            sequential with { Definition = sequential.Definition! with { AccountQuest = true } },
            sequential with { Definition = sequential.Definition! with { RequiredAnswers = [new(100,1)] } },
            sequential with { Definition = sequential.Definition! with { Exceptions = [new(0,1,null)] } },
            sequential with { Definition = sequential.Definition! with { MinimumClearCount = 1 } },
            sequential with { Definition = sequential.Definition! with { FirstExposedByNpc = 1 } },
            sequential with { Definition = sequential.Definition! with { GradeModifiers = ["[urgent]"] } }
        })
        {
            var pending = new QuestCatalog(new[] { unsupported, child1, child2 }.ToDictionary(quest => quest.Id));
            That(!QuestRelationProgress.DialogueChild(child2, pending) &&
                QuestRelationProgress.Refresh([new(0,200,17) { AcceptanceId = Guid.NewGuid() }], [201,202], pending)[0].ConditionState == 17,
                "未确认父任务规则、重复引用或缺失引用不能签发子任务入口及父归零");
        }
        That(QuestEligibility.InspectResource(first with { MinimumLevel = 254, MaximumLevel = 255 }).Entry == QuestResourceEntry.OutsideLevels &&
            QuestEligibility.InspectResource(first with { Definition = first.Definition! with { IsEvent = true } }).Entry == QuestResourceEntry.Event,
            "资源级排除分别记录原始特殊等级与活动启用限制");
        var hidden = first with { Definition = first.Definition! with { ExposedByNpc = false } };
        That(QuestEligibility.Evaluate(origin, hidden, catalog) is { Eligible: true, Visible: false } &&
            QuestEligibility.InspectResource(hidden).Entry == QuestResourceEntry.Dedicated,
            "隐藏入口保留定义和业务资格，不误认废弃或自动展示");
        That(!QuestEligibility.Evaluate(origin, first with { Definition = first.Definition! with { CreatureKind = 13 } }, catalog).Eligible,
            "宠物前提缺失不能当普通任务下发");
        var transferred = first with { RewardType = "[grow type]", Definition = first.Definition! with { JobChange = 1, GrowType = 1 } };
        That(QuestEligibility.Evaluate(origin, transferred, catalog).Eligible &&
            !QuestEligibility.Evaluate(origin with { GrowType = 1 }, transferred, catalog).Eligible,
            "转职任务的目标分支不冒充当前资格，已转职不能再接最终转职");
        var selfAwake = first with { Grade = "[realization]", Definition = first.Definition! with { JobChange = 2, GrowType = 0 } };
        That(QuestEligibility.Evaluate(origin with { Job = 9 }, selfAwake, catalog).Eligible &&
            !QuestEligibility.Evaluate(origin with { Job = 0 }, selfAwake, catalog).Eligible,
            "外传职业低位0的一觉资格与普通职业分开");
        var excluded = first with { Definition = first.Definition! with { NonTargetCharacters = [new("[at swordman]", -1, -1)] } };
        That(!QuestEligibility.Evaluate(origin with { Job = 11 }, excluded, catalog).Eligible &&
            QuestEligibility.Evaluate(origin, excluded, catalog).Eligible, "排除职业三元组不能反向当允许列表");
        var hardcore = first with { Definition = first.Definition! with { HardcoreOnly = true } };
        That(QuestEligibility.InspectResource(hardcore).Entry == QuestResourceEntry.Hardcore &&
            !QuestEligibility.Evaluate(origin, hardcore, catalog).Eligible, "尚未具备的极限模式不放行");
        var hide = first with { Definition = first.Definition! with { HideOnCompletedQuests = [202] } };
        That(QuestEligibility.Evaluate(origin with { CompletedQuests = [202] }, hide, catalog) is { Eligible: true, Visible: false },
            "资源隐藏条件影响展示，不冒充完成或废弃任务");
        var story=Quest(120) with {MinimumLevel=50,Condition="[clear map]",Rewards=[new(0,0,null),new(0,0,null)],
            Definition=new([]) {TargetCharacters=[new("[demonic lancer]",3,1)],PrerequisiteGroups=[[100]],Collisions=[121]}};
        var awakening=Quest(121) with {MinimumLevel=50,Condition="[clear map]",RewardType="[awakening type]",Rewards=[new(0,1,null)],
            Definition=new([]) {Job="[demonic lancer]",GrowType=3,JobChange=2,PrerequisiteGroups=[[100]]}};
        var storyCatalog=new QuestCatalog(new[]{first,story,awakening}.ToDictionary(row=>row.Id));
        var advanced=origin with {Job=13,GrowType=0x23,Level=95,CompletedQuests=[100]};
        That(QuestEligibility.Evaluate(advanced,story,storyCatalog) is {Eligible:true,Visible:true} &&
            QuestEligibility.Candidates(advanced,storyCatalog).Contains(120),"已二觉角色仍可见同职业普通主线替代分支");
        That(QuestEligibility.Evaluate(advanced with {GrowType=0x13},story,storyCatalog).Eligible &&
            !QuestEligibility.Evaluate(advanced with {GrowType=3},story,storyCatalog).Eligible,"保留一觉原资格，未觉醒不能借普通替代跳过觉醒");
        That(!QuestEligibility.Evaluate(advanced,awakening,storyCatalog).Eligible &&
            !QuestEligibility.Evaluate(advanced with {GrowType=0x22},story,storyCatalog).Eligible,"不重做觉醒或替其他职业分支取得衔接资格");
        That(!QuestEligibility.Evaluate(advanced with {CompletedQuests=[]},story,storyCatalog).Eligible &&
            !QuestEligibility.Evaluate(advanced with {CompletedQuests=[100,121]},story,storyCatalog).Eligible,"衔接仍检查真实前置和互斥已完成记录");
        var unrelated=new QuestCatalog(new[]{first,story,awakening with {Definition=awakening.Definition! with {PrerequisiteGroups=[[102]]}}}.ToDictionary(row=>row.Id));
        That(!QuestEligibility.Evaluate(advanced,story,unrelated).Eligible,"没有同前置觉醒互斥分支的普通任务保留精确觉醒条件");
        var transferStory=story with {MinimumLevel=15,Definition=story.Definition! with {JobChange=1,
            TargetCharacters=[new("[demonic lancer]",-1,-1)]}};
        var transferGoal=awakening with {MinimumLevel=15,RewardType="[grow type]",Rewards=[new(0,3,null)],
            Definition=new([]) {JobChange=1,TargetCharacters=[new("[demonic lancer]",0,-1)],PrerequisiteGroups=[[100]]}};
        var transferCatalog=new QuestCatalog(new[]{first,transferStory,transferGoal}.ToDictionary(row=>row.Id));
        That(QuestEligibility.Evaluate(advanced,transferStory,transferCatalog).Visible &&
            !QuestEligibility.Evaluate(advanced with {GrowType=0},transferStory,transferCatalog).Eligible,
            "GM已转职可以使用零奖励普通替代，未转职不能借其越过转职");
        var materials = Quest(123) with { Condition = "[seek n meet npc]", IntData = [3033,200,3034,200,3035,200,3036,200,1] };
        var materialCatalog = new QuestCatalog(new() { [materials.Id] = materials });
        var emptyInventory = DfoServer.Game.Inventory.Models.InventorySnapshot.Empty;
        That(QuestLifecyclePlan.ValidSeeking(materials) && QuestCoverage.HasOrdinaryDeclarations(materials) &&
            QuestLifecyclePlan.MissingMaterials(emptyInventory,materials,0) == 800 &&
            QuestLifecyclePlan.MaterialState(emptyInventory,materials,0) == 1,
            "四种材料各200保留完整800要求，type25材料通道只表示是否足额");
        var waiting = new ActiveMainQuest(0,123,512) { AcceptanceId = Guid.NewGuid() };
        That(QuestInventoryProgress.Refresh([waiting],emptyInventory,materialCatalog,0).Single().ConditionState == 513 &&
            QuestInventoryProgress.Refresh([waiting with { ConditionState = 514 }],emptyInventory,materialCatalog,0).Single() == (waiting with { ConditionState = 513 }) &&
            QuestInventoryProgress.Refresh([waiting with { ConditionState = 0 }],emptyInventory,materialCatalog,0).Single().ConditionState == 1,
            "大材料缺额和旧缺额计数归一后保留原接取及NPC位，材料丢失恢复1且保留已完成会面");
        var funded = emptyInventory;
        foreach (int id in new[] {3033,3034,3035,3036})
            funded = DfoServer.Game.Currency.CurrencyWallet.Set(funded,DfoServer.Game.Currency.CurrencyWallet.RewardKind(id)!.Value,200);
        That(QuestLifecyclePlan.MissingMaterials(funded,materials,0) == 0 &&
            QuestInventoryProgress.Refresh([waiting],funded,materialCatalog,0).Single().ConditionState == 512,
            "完整钱包材料满足仅清材料通道，不能替代真实NPC会面");
        var largeMaterials = materials with { IntData = [3033,int.MaxValue,3034,int.MaxValue,3033,int.MaxValue,1] };
        That(QuestLifecyclePlan.MissingMaterials(emptyInventory,largeMaterials,0) == 3L*int.MaxValue &&
            QuestLifecyclePlan.MaterialState(emptyInventory,largeMaterials,0) == 1 &&
            !QuestLifecyclePlan.ValidSeeking(materials with { IntData = [3033,0,1] }) &&
            !QuestLifecyclePlan.ValidSeeking(materials with { IntData = [3033,200,0] }),
            "重复材料及总缺额超过uint仍完整聚合，非法数量和NPC继续拒绝");
        StoryQuest Expert(int id, int type, params int[] collisions) => Quest(id) with
        {
            Condition = "[seeking]", SubType = -1, IntData = [3037, 100], MinimumLevel = 20, MaximumLevel = 99,
            Grade = "[common unique]", RewardType = "[expert job]", Difficulty = null, Rewards = [new(0, type, null)],
            Npc = 144, CompleteNpc = -1,
            Definition = new([]) { Job = "[all]", JobChange = 20, GrowType = -1, Collisions = collisions.ToImmutableArray() }
        };
        var enchanter = Expert(2702, 1, 2708, 2710, 2712);
        var alchemist = Expert(2708, 2, 2702);
        var expertCatalog = new QuestCatalog(new[] { enchanter, alchemist }.ToDictionary(row => row.Id));
        var qualified = origin with { Level = 20 };
        That(QuestExpertJobReward.Type(enchanter) == 1 && QuestExpertJobReward.Type(alchemist) == 2 &&
            QuestExpertJobReward.Type(enchanter with { Rewards = [new(0, 5, null)] }) is null &&
            QuestExpertJobReward.Type(enchanter with { Rewards = [new(0, 1, null), new(0, 1, null)] }) is null,
            "副职业奖励只接受一个 1–4 的类型整数");
        That(QuestEligibility.Evaluate(qualified, enchanter, expertCatalog).Eligible &&
            !QuestEligibility.Evaluate(origin, enchanter, expertCatalog).Eligible,
            "20 级可以接附魔师资格，1 级不行");
        That(!QuestEligibility.Evaluate(qualified with { CompletedQuests = [2702] }, alchemist, expertCatalog).Eligible &&
            !QuestEligibility.Evaluate(qualified with { ActiveMainQuests = [new(0, 2702, 1)] }, alchemist, expertCatalog).Eligible,
            "已接或已完成的副职业链挡住其他副职业第一步");
        foreach (int flag in new[] { 0, 1 })
        {
            var display = first with { Definition = first.Definition! with
                { FirstExposedByNpc = flag, Fields = [new("first exposed by npc", [new(0, flag, null)], [])] } };
            That(QuestCoverage.HasOrdinaryDeclarations(display), "首次NPC显示布尔不再误拒绝已有普通目标");
            That(!QuestEligibility.Evaluate(origin with { CompletedQuests = [100] }, display, catalog).Eligible &&
                !QuestEligibility.Evaluate(origin, display with { MinimumLevel = 50 }, catalog).Eligible,
                "显示声明不跳过已完成或等级资格");
            foreach (var invalid in new[]
            {
                display with { Definition = display.Definition! with { FirstExposedByNpc = 2,
                    Fields = [new("first exposed by npc", [new(0, 2, null)], [])] } },
                display with { Definition = display.Definition! with { Fields = [new("first exposed by npc", [new(6, 0, "1")], [])] } },
                display with { Definition = display.Definition! with { Fields = [.. display.Definition!.Fields, .. display.Definition.Fields] } }
            })
                That(!QuestCoverage.HasOrdinaryDeclarations(invalid), "首次NPC非法、文字和重复字段仍拒绝");
        }
        Console.WriteLine($"PASS {Count - checks} 任务资源入口、等级/职业及前置/子任务资格断言");
    }

    public static void RunFirstNpcResources(QuestCatalog catalog)
    {
        int checks = Count;
        var declared = catalog.All.Where(quest => quest.Definition?.Has("first exposed by npc") == true).OrderBy(quest => quest.Id).ToArray();
        That(declared.Select(quest => quest.Id).SequenceEqual(new[] { 8529, 8530, 8531, 8700, 8709, 8710, 8916, 8917, 8936, 20850 }),
            "新版PVF十条首次NPC显示声明完整盘点，不把其余活动或账号链误当已实现");
        foreach (var quest in declared)
        {
            That(QuestCoverage.HasFirstNpcExposureDeclaration(quest), "真实首次NPC显示布尔形状：" + quest.Id);
            if (quest.Id is 8709 or 8710)
                That(QuestEligibility.InspectResource(quest).Entry == QuestResourceEntry.Ordinary &&
                    QuestAccountPolicy.PaidSynchronization(quest) && !QuestCoverage.Unconfirmed(quest).Contains("synchronization account quest"),
                    "每周会面保持原账号声明并采用一次领奖事务：" + quest.Id);
            else
                That(QuestEligibility.InspectResource(quest).Entry == QuestResourceEntry.Event,
                    "首次NPC显示字段不移除原活动启用限制：" + quest.Id);
        }
        Console.WriteLine($"PASS {Count - checks} 真实PVF首次NPC显示声明及活动、账号边界断言");
    }

    internal static StoryQuest Quest(int id) => new(id, "fixture/" + id + ".qst", "[meet npc]", -1, [1], 1, 1,
        1, 99, [], null, null, null, [], null, null, [])
    {
        Grade = "[epic]", RewardType = "[item]", Difficulty = "1", Definition = new QuestDefinition([]) { Job = "[all]" }
    };
}
