using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// #256：黑市EXP 6051–6060的daily random set/next relation quest声明。
// 假设：两声明只是客户端分组元数据，服务端不实现随机选取/互斥/每日重置，任务按普通一次性[side]任务处理。
internal static class QuestHarlemExpTests
{
    static readonly int[] Group = Enumerable.Range(6051,10).ToArray();

    static StoryQuest Member(int id, bool daily = true, bool relation = true, int[]? list = null)
    {
        var fields = ImmutableArray.CreateBuilder<QuestField>();
        if (daily) fields.Add(new("daily random set",[],[]));
        if (relation)
            fields.Add(new("next relation quest",(list ?? Group.Where(other => other != id).ToArray()).Select(value => new QuestRewardToken(0,value,null)).ToImmutableArray(),[]));
        return QuestEligibilityTests.Quest(id) with { Grade = "[side]", Definition = new(fields.ToImmutable()) { Job = "[all]" } };
    }

    public static void RunCore()
    {
        int checks = Count;
        foreach (int id in Group)
        {
            var quest = Member(id);
            That(QuestCoverage.Unconfirmed(quest).IsEmpty && QuestCoverage.HasOrdinaryDeclarations(quest),"6051–6060精确分组声明视为已确认：" + id);
            QuestCoverage.RequireOrdinaryDeclarations(quest);
        }
        foreach (var invalid in new[]
        {
            // 同形状但不在该批任务内（id、等级分类、组员）仍保留限制。
            Member(100), Member(6050), Member(6061), Member(6051) with { Grade = "[daily]" }, Member(6051) with { Grade = "[daily random]" },
            // 声明缺一、重复、形状或组员不精确。
            Member(6051,daily:false), Member(6051,relation:false),
            Member(6051,list:[6052,6053]), Member(6051,list:[6051,..Group.Where(id => id != 6051)]),
            Member(6051,list:[..Group.Where(id => id != 6051),9999]), Member(6051,list:[..Group.Where(id => id != 6051).Reverse()]),
            Member(6051,list:[]),
            Member(6051) with { Definition = Member(6051).Definition! with { Fields = [..Member(6051).Definition!.Fields,new("daily random set",[],[])] } },
            Member(6051) with { Definition = Member(6051).Definition! with { Fields = [..Member(6051).Definition!.Fields,new("next relation quest",[new(0,6052,null)],[])] } },
            Member(6051) with { Definition = Member(6051).Definition! with { Fields = [new("daily random set",[new(0,1,null)],[]),Member(6051).Definition!.Fields[1]] } },
            Member(6051) with { Definition = Member(6051).Definition! with { Fields = [new("daily random set",[],[new("unknown",[],[])]),Member(6051).Definition!.Fields[1]] } },
            Member(6051) with { Definition = Member(6051).Definition! with { Fields = [Member(6051).Definition!.Fields[0],
                Member(6051).Definition!.Fields[1] with { Children = [new("unknown",[],[])] }] } },
            Member(6051) with { Definition = Member(6051).Definition! with { Fields = [Member(6051).Definition!.Fields[0],
                Member(6051).Definition!.Fields[1] with { Values = [..Member(6051).Definition!.Fields[1].Values.Take(8),new(6,0,"6060")] }] } },
        })
        {
            That(!QuestCoverage.HasOrdinaryDeclarations(invalid),
                "非精确分组声明不借用#256合同");
            Reject<NotSupportedException>(() => QuestCoverage.RequireOrdinaryDeclarations(invalid),"非精确声明仍明确拒绝");
        }
        Console.WriteLine($"PASS {Count - checks} 黑市EXP分组声明精确形状断言");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        var expected = new Dictionary<int,string>
        {
            [6051] = "[condition under clear]", [6052] = "[hunt monster]", [6053] = "[seeking]", [6054] = "[hunt monster]", [6055] = "[hunt monster]",
            [6056] = "[seeking]", [6057] = "[hunt monster]", [6058] = "[hunt monster]", [6059] = "[seeking]", [6060] = "[hunt monster]"
        };
        var character = new Character(1,0,"Exp",93,new Location(38,2,450,240),null) with { Level = 93, CompletedQuests = [6050] };
        var withoutPrerequisite = character with { CompletedQuests = [] };
        foreach (var (id,condition) in expected)
        {
            var quest = catalog.Get(id);
            That(quest.Condition == condition && quest.Grade == "[side]" && QuestCoverage.Unconfirmed(quest).IsEmpty &&
                quest.Definition!.Has("daily random set") && quest.Definition.Has("next relation quest"),
                "真实PVF中该批任务保留分组声明且不再有未确认声明：" + id);
            QuestCoverage.RequireOrdinaryDeclarations(quest);
            That(QuestEligibility.Evaluate(character,quest,catalog) is { Eligible: true, Visible: true } &&
                !QuestEligibility.Evaluate(withoutPrerequisite,quest,catalog).Eligible &&
                !QuestEligibility.Evaluate(character with { Level = 91 },quest,catalog).Eligible &&
                !QuestEligibility.Evaluate(character with { CompletedQuests = [6050,id] },quest,catalog).Eligible,
                "6050完成且92–94级可接，缺前置/等级不符/已完成仍拒绝（一次性，无每日重置）：" + id);
            // 假设的另一面：同组任务之间不互斥，已完成一个不阻止其它成员（服务端不猜选取规则）。
            That(QuestEligibility.Evaluate(character with { CompletedQuests = [6050,..expected.Keys.Where(other => other != id)] },quest,catalog).Eligible,
                "同组其它成员完成不改变本任务资格（未实现互斥）：" + id);
        }
        foreach (int id in new[] { 6051, 6052, 6054, 6055, 6057, 6058 })
            That(QuestHuntProgress.TryTargets(catalog.Get(id),out _) || QuestDungeonClearProgress.Supports(catalog.Get(id)),"目标沿已验证的猎杀/通关进度合同：" + id);
        // 目标侧：6053/6056/6059的seeking子类型0按精确形状接入（见RunSeeking）；6060的-2选择符没有语义依据，仍未接入。
        foreach (int id in new[] { 6053, 6056, 6059 })
            That(QuestCoverage.HasSupportedTarget(catalog.Get(id),catalog) && QuestLifecyclePlan.HarlemSeeking(catalog.Get(id)),"seeking子类型0目标已按精确形状接入：" + id);
        That(QuestCoverage.HasSupportedTarget(catalog.Get(6060),catalog) && QuestHuntProgress.TryTargets(catalog.Get(6060),out _),
            "6060的猎杀选择符-2按精英品级放行（见RunElite）");
        // 其余已知带这两声明的任务只有这批；其它任务的未确认声明不受影响。
        That(catalog.All.Where(quest => quest.Definition?.Has("daily random set") == true || quest.Definition?.Has("next relation quest") == true)
            .Select(quest => quest.Id).Order().SequenceEqual(Group),"资源中带这两个声明的只有6051–6060");
        Console.WriteLine($"PASS {Count - checks} 黑市EXP 6051–6060真实资源接取资格断言");
    }
    // #256：6053/6056/6059“seeking / sub type 0”。参考实现的寻物只读int data物品/数量对，不读sub type；仅对这三个精确形状放行。
    public static void RunSeeking(PvfArchive pvf, ItemResources items)
    {
        int checks = Count;
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf,learning);
        var character = new Character(1,0,"Exp",93,new Location(38,2,450,240),null) with { Level = 93, CompletedQuests = [6050], DungeonState = CharacterDungeonState.NewOrdinary, Learning = new(growth.GetLevelStartExperience(93), [new(0, 7, 9, [new(12, 5, 2)]), new(1, 29, 11, [new(7, 8, 3)])]) };
        var now = DateTimeOffset.UtcNow;
        var rewardItems = new Dictionary<int,int> { [6053] = 10306388, [6056] = 10306389, [6059] = 10306390 };
        foreach (var (id,material) in rewardItems)
        {
            var quest = catalog.Get(id);
            That(quest is { SubType: 0, Condition: "[seeking]" } && quest.IntData.SequenceEqual([material,3]) && QuestLifecyclePlan.HarlemSeeking(quest),
                "真实资源为seeking子类型0、单一材料×3并通过精确形状：" + id);
            var accept = QuestLifecyclePlan.Accept(character,quest,catalog,items.Items,items.GoldLimits,items.EpicPieces,now);
            var active = accept.ActiveAfter.Single(row => row.QuestId == id);
            That(active is { ConditionState: 1 } && active.AcceptanceId != Guid.Empty,"缺材料时可接取，初值为未达成1：" + id);
            // 库存驱动进度：3个归零，2个保持未达成，不累计获取次数。
            Character With(int count)
            {
                var grant = InventoryRewardGrantService.TryGrantBatch(character with { ActiveMainQuests = accept.ActiveAfter.ToArray() },
                    [new(material,count)],items.Items,items.GoldLimits,items.EpicPieces);
                That(grant.Accepted && grant.Plan is not null,"材料入包：" + id);
                return character with { ActiveMainQuests = accept.ActiveAfter.ToArray(), Inventory = grant.Plan!.After };
            }
            var three = With(3); var two = With(2);
            That(QuestInventoryProgress.Refresh(three.ActiveMainQuests,three.Inventory,catalog,now.ToUnixTimeSeconds()).Single(row => row.QuestId == id).ConditionState == 0 &&
                QuestInventoryProgress.Refresh(two.ActiveMainQuests,two.Inventory,catalog,now.ToUnixTimeSeconds()).Single(row => row.QuestId == id).ConditionState == 1,
                "持有3个材料才达成：" + id);
            // 完成交付：3个材料被消耗，奖励物品10306593入包，任务记为已完成。
            var done = three with { ActiveMainQuests = QuestInventoryProgress.Refresh(three.ActiveMainQuests,three.Inventory,catalog,now.ToUnixTimeSeconds()).ToArray() };
            var prepared = QuestCompletionPlan.Prepare(done,quest,ushort.MaxValue,catalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,now);
            That(prepared is { Outcome: QuestCompletionOutcome.Accepted, Plan: not null } && prepared.Plan.Consumed.Sum(row => (long)row.Count) == 3,
                "交付消耗3个材料并可提交：" + id + " " + prepared.Reason);
            var notEnough = QuestCompletionPlan.Prepare(two with { ActiveMainQuests = QuestInventoryProgress.Refresh(two.ActiveMainQuests,two.Inventory,catalog,now.ToUnixTimeSeconds()).ToArray() },
                quest,ushort.MaxValue,catalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,now);
            That(notEnough.Outcome != QuestCompletionOutcome.Accepted,"材料不足不能交付：" + id);
            // 掉落：仅7322内声明的五个怪物模板各自100%掉落该材料，其它模板/地图不掉。
            var bound = new DungeonEntryPlan(1,1,7322,0,byte.MaxValue,0,null!,null!,null!) { QuestAcceptances = [active] };
            var loaded = QuestDropResources.Create(character with { ActiveMainQuests = [active] },bound,catalog,items.Items,items.EpicPieces);
            var identity = new DungeonRunIdentity(1,0,1,Guid.NewGuid(),1);
            var drop = QuestDropPlan.Create(character with { ActiveMainQuests = [active] },bound,loaded,Guid.NewGuid(),identity,1,107010048,1,false,_ => 0,now);
            That(drop.Outcomes is [{ Granted: 1 }] && drop.Inventory.After.Items.Single().Core.ItemId == material,"7322内声明的领主怪死亡掉落1个材料：" + id);
            var none = QuestDropPlan.Create(character with { ActiveMainQuests = [active] },bound,loaded,Guid.NewGuid(),identity,1,107010099,1,false,_ => 0,now);
            That(none.Outcomes.Length == 0,"未声明的怪物模板不掉落：" + id);
            var full = QuestDropPlan.Create(three with { ActiveMainQuests = [active] },bound,loaded,Guid.NewGuid(),identity,1,107010048,1,false,_ => 0,now);
            That(full.Outcomes.Length == 0,"已持有目标数量(上限3)时不再掉落：" + id);
        }
        // 黑市EXP专用识别不承担普通sub0的总门禁；其它任务仍按自己的奖励、掉落及资格处理。
        foreach (int other in new[] { 2901, 2902, 10019, 4745 })
            if (catalog.TryGet(other,out var quest))
                That(!QuestLifecyclePlan.HarlemSeeking(quest) &&
                    QuestLifecyclePlan.SeekingSubtype(quest) && QuestLifecyclePlan.ValidSeeking(quest),
                    "合法sub0目标不因缺少黑市或每日任务编号白名单被误拒：" + other);
        var real = catalog.Get(6053);
        foreach (var altered in new[]
        {
            real with { Id = 6054 }, real with { Grade = "[daily mission]" }, real with { SubType = 25 }, real with { IntData = [10306388,4] },
            real with { IntData = [10306388,3,10306389,3] }, real with { RewardType = "[exp]" }, real with { MainQuest = 6052 },
            real with { Definition = real.Definition! with { Drops = [] } },
            real with { Definition = real.Definition! with { Drops = [real.Definition.Drops[0] with { ItemId = 10306389 }] } },
            real with { Definition = real.Definition! with { Drops = [real.Definition.Drops[0] with { DungeonId = 7323 }] } },
            real with { Definition = real.Definition! with { Drops = [real.Definition.Drops[0] with { Rate = 50 }] } },
        })
            That(!QuestLifecyclePlan.HarlemSeeking(altered) &&
                QuestLifecyclePlan.SeekingSubtype(altered) == (altered.SubType is -1 or 0),
                "改动资源不借用黑市专用识别，目标子型与奖励、资格和掉落独立校验");
        Console.WriteLine($"PASS {Count - checks} 黑市EXP seeking子类型0（6053/6056/6059）接取、掉落、进度与交付断言");
    }

    // #256：6060“除掉战争的元凶”(7322 -1 -2 9)。-2 = 精英（生成类型 1 冠军 / 2 超级冠军），不是 named 名单。
    public static void RunElite(PvfArchive pvf, ItemResources items)
    {
        int checks = Count;
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf,learning);
        var quest = catalog.Get(6060);
        That(quest is { Condition: "[hunt monster]", Grade: "[side]" } && quest.IntData.SequenceEqual([7322,-1,-2,9]),"真实6060为 7322 -1 -2 9");
        That(QuestHuntProgress.TryTargets(quest,out var targets) && targets is [{ DungeonId: 7322, MinimumDifficulty: -1, MonsterId: -2, Count: 9 }] &&
            QuestCoverage.HasSupportedTarget(quest,catalog),"6060可接（目标被识别）");
        // 范围限定：同形状但不是6060、改地下城/条件/品级的任务一律仍不放行。
        foreach (var other in new[] { quest with { Id = 6061 }, quest with { IntData = [7323,-1,-2,9] }, quest with { Grade = "[sub]" },
            quest with { Condition = "[hunt enemy]", IntData = [7322,-1,-2,1,9] } })
            That(!QuestHuntProgress.TryTargets(other,out _),"-2只放行真实6060，其它任务不受影响");
        foreach (var other in catalog.All.Where(row => row.Id != 6060 && row.Condition is "[hunt monster]" or "[hunt enemy]" && row.IntData.Contains(-2)))
            That(!QuestHuntProgress.TryTargets(other,out _),"其它含-2的猎杀任务不放行：" + other.Id);
        var initial = QuestHuntProgress.Initial(quest);
        That(initial == 9u,"初值为9");
        var active = new ActiveMainQuest(5,6060,initial) { AcceptanceId = Guid.NewGuid() };
        var entry = new DungeonEntryPlan(1,1,7322,6060,0,0,null!,null!,null!) { QuestAcceptances = [active] };
        DungeonPlannedMonster Mob(int id, byte spawn = 0) => new(0,1,id,93,null) { SpawnType = spawn };
        var after = QuestHuntProgress.MonsterDeath([active],entry,catalog,Mob(500,1))[0];
        That(after.ConditionState == 8u && after.AcceptanceId == active.AcceptanceId,"杀冠军精英推进一次");
        That(QuestHuntProgress.MonsterDeath([active],entry,catalog,Mob(500,2))[0].ConditionState == 8u,"超级冠军同样推进");
        That(QuestHuntProgress.MonsterDeath([active],entry,catalog,Mob(999))[0] == active &&
            QuestHuntProgress.MonsterDeath([active],entry,catalog,Mob(999,3))[0] == active,"杀普通怪或Boss不推进");
        That(QuestHuntProgress.MonsterDeath([active],entry with { DungeonId = 3 },catalog,Mob(500,1))[0] == active,"其它副本的精英怪不推进");
        That(QuestHuntProgress.MonsterDeath([active with { AcceptanceId = Guid.NewGuid() }],entry,catalog,Mob(500))[0].ConditionState == 9u,"旧接取不推进");
        var apc = Mob(500) with { AiCharacter = new(500,[],"[monster]","[named]","aicharacter/fixture.aic",1), SpawnType = 5 };
        That(QuestHuntProgress.MonsterDeath([active],entry,catalog,apc)[0] == active,"APC不计");
        var progress = active;
        for (int index = 0; index < 9; index++)
        {
            That(progress.ConditionState == 9u - index,"逐次推进：" + index);
            progress = QuestHuntProgress.MonsterDeath([progress],entry with { QuestAcceptances = [progress] },catalog,Mob(500 + index % 2, 1))[0];
        }
        That(progress.ConditionState == 0,"满9次达成");
        That(QuestHuntProgress.MonsterDeath([progress],entry with { QuestAcceptances = [progress] },catalog,Mob(500, 1))[0] == progress,"达成后不下溢");
        var character = new Character(1,0,"Exp",93,new Location(38,2,450,240),null) with { Level = 93, CompletedQuests = [6050],
            ActiveMainQuests = [progress], DungeonState = CharacterDungeonState.NewOrdinary,
            Learning = new(growth.GetLevelStartExperience(93), [new(0, 7, 9, [new(12, 5, 2)]), new(1, 29, 11, [new(7, 8, 3)])]) };
        var now = DateTimeOffset.UtcNow;
        var done = QuestCompletionPlan.Prepare(character,quest,ushort.MaxValue,catalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,now);
        That(done is { Outcome: QuestCompletionOutcome.Accepted, Plan: not null },"满9次可交付：" + done.Reason);
        var partial = QuestCompletionPlan.Prepare(character with { ActiveMainQuests = [active with { ConditionState = 3 }] },quest,ushort.MaxValue,catalog,
            parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,now);
        That(partial.Outcome != QuestCompletionOutcome.Accepted,"未满9次不可交付");
        Console.WriteLine($"PASS {Count - checks} 黑市EXP 6060（-2=精英品级）接取、推进与交付通过");
    }
}
