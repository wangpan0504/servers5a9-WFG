using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestCharacterRewardTests
{
    public static void RunCore()
    {
        int checks = Count;
        var quest = QuestEligibilityTests.Quest(100) with
        {
            Attributes = ["slot expansion"],
            Definition = new([
                new("exception filter",[new(6,0,"character")],[]),
                new("exception quest",[new(6,0,"[urgent]")],[]),
                new("attribute",[new(6,0,"slot expansion")],[])])
            { Job = "[all]", Exceptions = [new(6,0,"[urgent]")] }
        };
        That(QuestCoverage.HasOrdinaryDeclarations(quest),"已确认角色分类、urgent例外与槽位属性沿普通任务合同");
        var catalog = new QuestCatalog(new() { [100] = quest });
        var character = new Character(1,0,"Slots",1,new Location(38,2,450,240),null);
        for (byte job = 0; job < 16; job++)
            That(QuestEligibility.Evaluate(character with { Job = job, EquipmentSlotFlags = 0x13 },quest,catalog).Visible,
                "分类及属性不猜测装备槽未解锁资格，保留所有职业实际资格 " + job);
        foreach (var invalid in new[]
        {
            Field("exception filter",[new(6,0,"future")]), Field("exception filter",[new(0,3,null)]),
            Field("exception filter",[]), Field("exception filter",[new(6,0,"character"),new(6,0,"character")]),
            Field("exception filter",[new(6,0,"character")],[new("unknown",[],[])]),
            Field("exception quest",[new(6,0,"[future]")]), Field("exception quest",[new(0,0,null)]),
            Field("exception quest",[]), Field("exception quest",[new(6,0,"[urgent]"),new(6,0,"[urgent]")]),
            Field("exception quest",[new(6,0,"[urgent]")],[new("unknown",[],[])]),
            Field("attribute",[new(6,0,"future")]), Field("attribute",[new(0,29,null)]),
            Field("attribute",[new(6,0,"slot expansion")],[new("unknown",[],[])])
        })
        {
            That(!QuestCoverage.HasOrdinaryDeclarations(invalid),"未知分类、例外、属性或形状不借用已确认合同");
            Reject<NotSupportedException>(() => QuestCoverage.RequireOrdinaryDeclarations(invalid),"声明失败有明确限制");
        }
        That(QuestEligibility.InspectResource(Field("exception filter",[new(6,0,"future")])) is
            { Entry: QuestResourceEntry.Dedicated, Reason: "exception filter需要已确认的单字符串character声明" },
            "未确认filter保留专用入口与字段限制");
        var searching = new QuestField("searching information",[new(0,0,null)],[]);
        var noAuto = new QuestField("attribute",[new(6,0,"not check auto clear")],[]);
        var explicitMeet = QuestEligibilityTests.Quest(101) with
        {
            Attributes = ["not check auto clear"], Definition = new([searching,noAuto]) { Job = "[all]" }
        };
        That(QuestCoverage.HasOrdinaryDeclarations(explicitMeet) && QuestCoverage.Unconfirmed(explicitMeet).IsEmpty,
            "显式NPC会面保留searching0与禁止自动检查的精确声明");
        foreach (var invalid in new[]
        {
            explicitMeet with { Condition = "[play only]" }, explicitMeet with { SubType = 1 },
            explicitMeet with { IntData = [0] }, explicitMeet with { IntData = [1,2] },
            explicitMeet with { Attributes = [] }, explicitMeet with { Attributes = ["not check auto clear","future"] },
            Changed([noAuto]), Changed([searching,searching,noAuto]), Changed([searching,noAuto,noAuto]),
            Changed([searching with { Values = [] },noAuto]),
            Changed([searching with { Values = [new(0,1,null)] },noAuto]),
            Changed([searching with { Values = [new(6,0,"0")] },noAuto]),
            Changed([searching with { Values = [new(0,0,null),new(0,0,null)] },noAuto]),
            Changed([searching with { Children = [new("unknown",[],[])] },noAuto]),
            Changed([searching,noAuto with { Values = [new(6,0,"not check auto clear"),new(6,0,"slot expansion")] }])
        })
        {
            That(!QuestCoverage.HasOrdinaryDeclarations(invalid),"其它目标、重复或不匹配声明不能借用显式NPC合同");
            Reject<NotSupportedException>(() => QuestCoverage.RequireOrdinaryDeclarations(invalid),"精确声明不符仍拒绝");
        }
        StoryQuest Changed(ImmutableArray<QuestField> fields) => explicitMeet with
            { Definition = explicitMeet.Definition! with { Fields = fields } };
        var empty = ReadPrerequisites(false); var mixed = ReadPrerequisites(true);
        var prerequisites = new QuestCatalog(new() { [100] = empty, [103] = QuestEligibilityTests.Quest(103) });
        That(empty.Definition is { Diagnostics.Length: 0, PrerequisiteGroups.Length: 0 } &&
            empty.Definition.Has("pre required quest") && QuestEligibility.Evaluate(character,empty,prerequisites).Eligible,
            "空前置声明保留原文并按external忽略，不阻断正常资格");
        That(mixed.Definition is { Diagnostics.Length: 0, PrerequisiteGroups.Length: 1 } &&
            mixed.Definition.PrerequisiteGroups[0].SequenceEqual([103]) &&
            !QuestEligibility.Evaluate(character,mixed,prerequisites).Eligible &&
            QuestEligibility.Evaluate(character with { CompletedQuests = [103] },mixed,prerequisites).Eligible,
            "空OR组不能绕过保留下来的真实非空前置");
        foreach (string name in new[] { "male depend message", "male solve message" })
        {
            var text = quest with { Definition = quest.Definition! with
            {
                Fields = quest.Definition!.Fields.Add(new(name,[new(6,0,"实际对话文本")],[]))
            } };
            That(QuestCoverage.Unconfirmed(text).Length == 0 && QuestCoverage.HasOrdinaryDeclarations(text),
                "匹配客户端单文本显示声明不再被当作未知业务: " + name);
            for (byte job = 0; job < 16; job++)
                That(QuestEligibility.Evaluate(character with { Job = job },text,catalog).Visible,
                    "显示文本不新增职业资格限制: " + name + "/" + job);
            foreach (var field in new QuestField[]
            {
                new(name,[],[]), new(name,[new(0,1,null)],[]), new(name,[new(6,0,null)],[]),
                new(name,[new(6,0,"a"),new(6,0,"b")],[]),
                new(name,[new(6,0,"a")],[new("unknown",[],[])])
            })
            {
                var invalid = text with { Definition = text.Definition! with
                {
                    Fields = quest.Definition!.Fields.Add(field)
                } };
                That(!QuestCoverage.HasOrdinaryDeclarations(invalid),"男性文本仍要求已确认的单文本形状: " + name);
                Reject<NotSupportedException>(() => QuestCoverage.RequireOrdinaryDeclarations(invalid),"显示声明形状失败有明确限制");
            }
        }
        foreach (string name in new[] { "visible npc", "show click on complete npc", "dialog show time" })
        {
            foreach (int npc in new[] { -2, -1, 0, 127 })
            {
                var display = quest with { Definition = quest.Definition! with
                    { Fields = quest.Definition!.Fields.Add(new(name,[new(0,npc,null)],[])) } };
                That(QuestCoverage.Unconfirmed(display).Length == 0 && QuestCoverage.HasOrdinaryDeclarations(display),
                    "客户端显示声明保留有符号单整数，不增加资格或进度限制 " + name + "/" + npc);
                for (byte job = 0; job < 16; job++)
                    That(QuestEligibility.Evaluate(character with { Job = job },display,catalog).Visible,
                        "客户端显示声明使用统一职业资格 " + name + "/" + npc + "/" + job);
            }
            foreach (var field in new QuestField[]
            {
                new(name,[],[]), new(name,[new(6,0,"127")],[]),
                new(name,[new(0,127,null),new(0,128,null)],[]),
                new(name,[new(0,127,null)],[new("unknown",[],[])])
            })
            {
                var invalid = quest with { Definition = quest.Definition! with { Fields = quest.Definition!.Fields.Add(field) } };
                That(!QuestCoverage.HasOrdinaryDeclarations(invalid),"NPC显示声明仍要求匹配单整数形状: " + name);
                Reject<NotSupportedException>(() => QuestCoverage.RequireOrdinaryDeclarations(invalid),"错误NPC显示形状保留明确限制");
            }
        }
        var maleDialog = new QuestField("male dialog",
            Enumerable.Range(0,8).Select(value => new QuestRewardToken(0,value,null)).Append(new(6,0,"原始男性对话")).ToImmutableArray(),[]);
        var dialog = quest with { Definition = quest.Definition! with { Fields = quest.Definition!.Fields.Add(maleDialog) } };
        That(QuestCoverage.HasOrdinaryDeclarations(dialog),"八整数及单文本男性对话沿普通任务显示合同");
        foreach (var invalid in new[]
        {
            maleDialog with { Values = maleDialog.Values.RemoveAt(0) },
            maleDialog with { Values = maleDialog.Values.Add(new(0,1,null)) },
            maleDialog with { Values = maleDialog.Values.SetItem(0,new(6,0,"1")) },
            maleDialog with { Values = maleDialog.Values.SetItem(8,new(0,1,null)) },
            maleDialog with { Children = [new("unknown",[],[])] }
        })
            That(!QuestCoverage.HasOrdinaryDeclarations(quest with { Definition = quest.Definition! with
                { Fields = quest.Definition!.Fields.Add(invalid) } }),"未确认男性对话形状保留明确限制");
        var notification = new QuestField("quest clear noti",[],
            [new("image info",[new(6,0,"Interface\\status_icon.img"),new(0,240,null)],[]),
             new("noti message",[new(6,0,"舒服的风从四周吹来。")],[])]);
        var notified = quest with { Definition = quest.Definition! with { Fields = quest.Definition!.Fields.Add(notification) } };
        That(QuestCoverage.Unconfirmed(notified).Length == 0 && QuestCoverage.HasOrdinaryDeclarations(notified),
            "本地任务完成图标文字声明沿已确认显示合同");
        That(QuestCoverage.IsClearNotification(notification with { Children = [notification.Children[1],notification.Children[0]] }),
            "完成提示按子标签识别，保留资源顺序而不依赖固定排列");
        for (byte job = 0; job < 16; job++)
            That(QuestEligibility.Evaluate(character with { Job = job },notified,catalog).Visible,
                "任务完成提示不增加职业资格限制 " + job);
        var image = notification.Children[0]; var message = notification.Children[1];
        foreach (var invalid in new QuestField[]
        {
            notification with { Values = [new(0,1,null)] },
            notification with { Children = [image] }, notification with { Children = [image,image] },
            notification with { Children = [image,message,new("unknown",[],[])] },
            notification with { Children = [image,message with { Children = [new("unknown",[],[])] }] },
            notification with { Children = [image with { Values = [new(6,0,null),new(0,240,null)] },message] },
            notification with { Children = [image with { Values = [new(6,0,"a"),new(6,0,"240")] },message] },
            notification with { Children = [image with { Values = [new(6,0,"a"),new(0,-1,null)] },message] },
            notification with { Children = [image,message with { Values = [] }] },
            notification with { Children = [image,message with { Values = [new(0,1,null)] }] },
            notification with { Children = [image,message with { Values = [new(6,0,null)] }] },
            notification with { Children = [image,message with { Values = [new(6,0,"a"),new(6,0,"b")] }] }
        })
        {
            var wrong = notified with { Definition = notified.Definition! with { Fields = quest.Definition!.Fields.Add(invalid) } };
            That(!QuestCoverage.HasOrdinaryDeclarations(wrong),"完成提示仍要求单图标与单文本的已确认形状");
            Reject<NotSupportedException>(() => QuestCoverage.RequireOrdinaryDeclarations(wrong),"未知完成提示形状保留明确限制");
        }
        Console.WriteLine($"PASS {Count-checks} 槽位任务声明、显示、分类及空前置合同断言");

        StoryQuest Field(string name,ImmutableArray<QuestRewardToken> values,ImmutableArray<QuestField> children = default)
        {
            var definition = quest.Definition!;
            return quest with { Definition = definition with
            {
                Fields = definition.Fields.Select(field => field.Name == name ? new(name,values,children.IsDefault ? [] : children) : field).ToImmutableArray(),
                Exceptions = name == "exception quest" ? values : definition.Exceptions
            } };
        }
        static StoryQuest ReadPrerequisites(bool mixed)
        {
            var strings = new Dictionary<int,string> { [1] = "[level]", [2] = "[type]", [3] = "[meet npc]", [4] = "[pre required quest]" };
            var tokens = new List<PvfToken> { new(3,1),new(0,1),new(0,99),new(3,2),new(6,3),new(3,4) };
            if (mixed) { tokens.Add(new(3,4)); tokens.Add(new(0,103)); }
            return QuestDefinitionReader.Read(100,"fixture/slots.qst",tokens,id => strings[id]);
        }
    }

    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        int checks = Count;
        var skills = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf); var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var empty = QuestEligibilityTests.Quest(100) with { Npc = 2, CompleteNpc = 2, IntData = [2], Attributes = ["not give exp quest"] };
        var range = empty with { Id = 101, Condition = "[reach the range]", SubType = 1, CompleteNpc = 0,
            IntData = [40,2,254,218,100,145] };
        var cinematic = empty with { Id = 102, Condition = "[look cinematic]", CompleteNpc = 0, IntData = [1],
            CinematicPath = "fixture/scene.cmt", Definition = new([new("go guide",
                [new(0,40,null),new(0,2,null),new(0,28,null),new(0,-1,null),new(6,0,"前往场景")],[])]) { Job = "[all]" } };
        var noNpc = empty with { Id = 103, CompleteNpc = 0 };
        var wrongRange = range with { Id = 104, IntData = [40,2,254,218,0,145] };
        var sceneCoverage = QuestCoverage.Scan(new(new[] { range,cinematic,noNpc,wrongRange }.ToDictionary(quest => quest.Id)),
            items.Items,items.EpicPieces,parameters);
        That(sceneCoverage.Quests.Where(row => row.Id is 101 or 102 or 103).All(row => row.ImplementedPath) &&
            sceneCoverage.Quests.Single(row => row.Id == 104) is { ImplementedPath: false } unsupported &&
            unsupported.Restrictions.Contains("目标子类型尚未接入"),
            "已确认目标不因完成NPC为0而限制交付；未知范围仍按目标形状限制");
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        var clearNotifications = catalog.All.Where(quest => quest.Definition?.Has("quest clear noti") == true).ToArray();
        That(clearNotifications.Length > 0 && clearNotifications.All(QuestCoverage.HasOrdinaryDisplay),
            "实际完成提示资源均使用已确认的图标路径、非负序号和文字形状");
        That(catalog.All.Where(quest => quest.RewardType == "[slot expansion]" && QuestCharacterReward.TryScalar(quest,out _))
            .Select(quest => quest.Rewards[0].Value).Distinct().Order().SequenceEqual([0,1,2]),"三个装备槽奖励使用实际标量而非固定任务编号");
        That(catalog.All.Where(quest => quest.RewardType == "[tp]").All(quest => quest.Rewards.Count == 0 &&
            !QuestCharacterReward.TryScalar(quest,out _)),"本版空参数TP奖励不套用不存在的正整数声明，不伪造额外来源");
        // 深渊派对挑战资格：只精确放行 [hell challenge] 参数1，其余专用奖励类型仍被拒
        var hell = catalog.All.Where(quest => quest.RewardType == "[hell challenge]").ToArray();
        That(hell.Length > 0 && hell.All(quest => QuestCharacterReward.TryHellChallenge(quest) == (quest.Rewards is [{ Type: 0, Text: null, Value: 1 }])),
            "深渊派对挑战资格只在奖励参数为1且无选择奖励时放行");
        That(catalog.All.Where(quest => quest.Id != 6081 && quest.RewardType is "[hell legend]" or "[operation hope]" or "[tp]" or "[slot expansion]")
            .All(quest => !QuestCharacterReward.TryHellChallenge(quest)),"其他专用奖励类型不得被深渊资格放行");
        var hellProbe = hell.First(QuestCharacterReward.TryHellChallenge);
        That(!QuestCharacterReward.TryHellChallenge(hellProbe with { Rewards = [new(0,2,null)] }) &&
            !QuestCharacterReward.TryHellChallenge(hellProbe with { Rewards = [new(0,1,null),new(0,0,null)] }) &&
            !QuestCharacterReward.TryHellChallenge(hellProbe with { RewardType = "[hell legend]" }),
            "参数不是1、带额外参数或换成其他奖励类型都不得放行");
        // [inferno challenge]：7527/7530/7533/7539/7542 单个 Type0 Text null Value>0 奖励，行为同 hell challenge。
        // 注意：7527/7530/7533/7539/7542 是 reward int data 的值，不是任务号（如 5783→7542、10106→7530）。
        var infernos = catalog.All.Where(quest => quest.RewardType == "[inferno challenge]").ToArray();
        That(infernos.Length > 0 && new[] { 7527, 7530, 7533, 7539, 7542 }.All(value => infernos.Any(quest => quest.Rewards is [{ Value: var v }] && v == value)),
            "真实资源含奖励值 7527/7530/7533/7539/7542 的 inferno 任务");
        foreach (var inferno in infernos)
        {
            That(QuestCharacterReward.TryHellChallenge(inferno) == (inferno.Rewards is [{ Type: 0, Text: null, Value: > 0 }] && inferno.Definition is { SelectRewards.Length: 0 }),
                "inferno 任务按形状放行：" + inferno.Id);
            if (!QuestCharacterReward.TryHellChallenge(inferno)) continue;
            var infernoRow = QuestCoverage.Scan(new(new() { [inferno.Id] = inferno }),items.Items,items.EpicPieces,parameters).Quests.Single();
            That(!infernoRow.Restrictions.Any(text => text.Contains("奖励依赖对应挑战或深渊资格") || text.StartsWith("固定奖励需要")),
                "inferno 任务覆盖报告不再提示奖励未实现：" + inferno.Id + " " + string.Join("|",infernoRow.Restrictions));
        }
        That(new[] { 5783, 10106 }.All(id => QuestCharacterReward.TryHellChallenge(catalog.Get(id))),"5783/10106 inferno 放行");
        var infernoProbe = catalog.Get(10106);
        That(!QuestCharacterReward.TryHellChallenge(infernoProbe with { Rewards = [new(0,0,null)] }) &&
            !QuestCharacterReward.TryHellChallenge(infernoProbe with { Rewards = [new(0,1,null),new(0,1,null)] }) &&
            !QuestCharacterReward.TryHellChallenge(infernoProbe with { Rewards = [new(1,1,null)] }) &&
            !QuestCharacterReward.TryHellChallenge(infernoProbe with { Definition = infernoProbe.Definition! with { SelectRewards = [new(0,1,null)] } }) &&
            !QuestCharacterReward.TryHellChallenge(infernoProbe with { RewardType = "[hell legend]" }) &&
            QuestCharacterReward.TryHellChallenge(infernoProbe with { RewardType = "[cosmo challenge]" }) &&
            !QuestCharacterReward.TryHellChallenge(infernoProbe with { RewardType = "[cosmo challenge]", Rewards = [new(0,0,null)] }) &&
            !QuestCharacterReward.TryHellChallenge(hellProbe with { Rewards = [new(0,0,null)] }),
            "inferno 负例：值为0、多奖励行、非Type0、带选择奖励、hell legend/cosmo challenge 仍拒绝，hell challenge 仍要求值为1");
        var hellCoverage = QuestCoverage.Scan(new(catalog.All.Where(quest => quest.RewardType is "[hell challenge]" or "[hell legend]" or "[cosmo challenge]")
            .ToDictionary(quest => quest.Id)),items.Items,items.EpicPieces,parameters);
        const string stateRestriction = "奖励依赖对应挑战或深渊资格";
        That(hellCoverage.Quests.All(row => QuestCharacterReward.TryHellChallenge(catalog.Get(row.Id)) ?
            !row.Restrictions.Any(text => text.Contains(stateRestriction) || text.StartsWith("固定奖励需要")) :
            row.Restrictions.Any(text => text.Contains(stateRestriction))),
            "只有参数1的深渊资格任务不再被奖励类型/固定奖励解析拒绝，其余仍报专用状态未接入");
        // 原报告六项必须逐项执行，不能被Take筛选或缺少6045前置定义悄悄跳过。
        int[] reportedHell = [6880,4391,4539,2002,2900,6080];
        var hellDefinitions = hell.Concat(hell.SelectMany(quest => quest.Prerequisites).Select(catalog.Get)).DistinctBy(quest => quest.Id).ToArray();
        var exactHellCoverage = QuestCoverage.Scan(new(hellDefinitions.ToDictionary(quest => quest.Id)),items.Items,items.EpicPieces,parameters);
        That(reportedHell.All(id => exactHellCoverage.Quests.Single(row => row.Id == id).ImplementedPath &&
            catalog.Get(id) is { Condition: "[meet npc]", SubType: 1, IntData: [45] } && QuestCharacterReward.TryHellChallenge(catalog.Get(id))),
            "Ep1至Ep6保持完整实际声明，全部具有可执行交付路径");
        var samples = hell.Where(QuestCharacterReward.TryHellChallenge).Where(quest => QuestCoverage.Scan(new(new() { [quest.Id] = quest }),
            items.Items,items.EpicPieces,parameters).Quests.Single().ImplementedPath).Take(3);
        var exercisedHell = samples.Concat(reportedHell.Select(catalog.Get)).DistinctBy(quest => quest.Id).ToArray();
        int hellDone = 0;
        foreach (var quest in exercisedHell)
        {
            byte level = (byte)Math.Max(quest.MinimumLevel,1);
            using var f = new QuestOrdinaryCompletionTests.Fixture(towns,characters,skills,growth,parameters,items,quest,
                materialCount: 0,definitions: hellDefinitions,active: [],useCycles: true,configure: candidate =>
                {
                    var learning = SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,level,
                        candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(level) },skills);
                    return candidate with { Level = level, CompletedQuests = quest.Prerequisites.ToArray(), InitialLearning = learning,
                        Skills = learning.Trees[0].Skills.Select(skill => new CharacterSkill(skill.Id,skill.Level)).OrderBy(skill => skill.Id).ToArray() };
                });
            That(f.Change((ushort)quest.Id,QuestLifecycleAction.Accept)?.Commit is not null,"深渊资格任务可正常接取 " + quest.Id);
            var finished = f.Finish(ushort.MaxValue,(ushort)quest.Id);
            That(finished.Receipt?.Commit is { HellChallenge: true } commit && commit.Character.CompletedQuests.Contains(quest.Id) &&
                commit.Character.ActiveMainQuests.Length == 0 && f.Receipts == 1,"深渊资格任务可交付并记入已完成 " + quest.Id);
            That(f.Finish(ushort.MaxValue,(ushort)quest.Id).Outcome == QuestFinishOutcome.AlreadyCompleted && f.Receipts == 1,"深渊资格任务不可重复交付 " + quest.Id);
            hellDone++;
        }
        That(hellDone == exercisedHell.Length && reportedHell.All(id => exercisedHell.Any(quest => quest.Id == id)),
            "原有代表及报告六项全部走完实际接取到交付，没有按覆盖结果跳过失败项");

        // 深渊派对入口资格（假设：完成任一确认形态的 [hell challenge] 任务即具备；无原服依据，Refs #256）。
        var qualifier = hell.First(QuestCharacterReward.TryHellChallenge);
        var qualBase = new Character(1,0,"Hell",1,towns.Room,null);
        That(!QuestCharacterReward.HasHellPartyQualification(qualBase,catalog) &&
            !QuestCharacterReward.HasHellPartyQualification(qualBase with { CompletedQuests = [999999] },catalog) &&
            QuestCharacterReward.HasHellPartyQualification(qualBase with { CompletedQuests = [999999,qualifier.Id] },catalog),
            "无完成记录或只有未知任务不具备深渊资格，完成资格任务后具备");
        That(!QuestCharacterReward.HasHellPartyQualification(qualBase with { CompletedQuests = [6081] },catalog) &&
            catalog.All.Where(quest => quest.RewardType is "[inferno challenge]" or "[cosmo challenge]").Take(3)
                .All(quest => !QuestCharacterReward.HasHellPartyQualification(qualBase with { CompletedQuests = [quest.Id] },catalog)),
            "6081/inferno/cosmo 的假设性放行不授予深渊派对资格");
        // [cosmo challenge]（含10112）同 inferno 规则放行；真实资源中全部满足形状的都应放行。
        That(catalog.Get(10112) is { RewardType: "[cosmo challenge]" } cosmo && cosmo.Rewards is [{ Value: 7191 }] && QuestCharacterReward.TryHellChallenge(cosmo) &&
            QuestCoverage.Scan(new(new() { [10112] = cosmo }),items.Items,items.EpicPieces,parameters).Quests.Single() is { } cosmoRow &&
            !cosmoRow.Restrictions.Any(text => text.Contains("奖励依赖对应挑战或深渊资格")),"10112 [cosmo challenge] 7191 放行且覆盖提示已去除 " + string.Join("|",QuestCoverage.Scan(new(new() { [10112] = catalog.Get(10112) }),items.Items,items.EpicPieces,parameters).Quests.Single().Restrictions));
        // 6081 [hell legend] 291100268：假设性放行，仅限6081（无原服依据，Refs #256）；其他 hell legend、值不同、带选择奖励、多奖励行仍拒绝。
        var legend = catalog.All.Where(quest => quest.RewardType == "[hell legend]").ToArray();
        That(legend.Select(quest => quest.Id).SequenceEqual([6081]) && catalog.Get(6081) is { Rewards: [{ Type: 0, Text: null, Value: 291100268 }] } q6081 &&
            QuestCharacterReward.TryHellChallenge(q6081),"6081 hell legend 放行");
        var q6081Probe = catalog.Get(6081);
        That(!QuestCharacterReward.TryHellChallenge(q6081Probe with { Id = 6082 }) &&
            !QuestCharacterReward.TryHellChallenge(q6081Probe with { Rewards = [new(0,291100267,null)] }) &&
            !QuestCharacterReward.TryHellChallenge(q6081Probe with { Rewards = [new(0,291100268,null),new(0,291100268,null)] }) &&
            !QuestCharacterReward.TryHellChallenge(q6081Probe with { Rewards = [new(0,291100268,"x")] }) &&
            !QuestCharacterReward.TryHellChallenge(q6081Probe with { Definition = q6081Probe.Definition! with { SelectRewards = [new(0,1,null)] } }),
            "6081 负例：其他任务号、值不同、多奖励行、带文本、带选择奖励仍拒绝");
        That(catalog.Get(10112).Prerequisites.Contains(6081),"10112 前置链指向 6081");
        That(!QuestCoverage.Scan(new(new() { [6081] = q6081Probe }),items.Items,items.EpicPieces,parameters).Quests.Single().Restrictions.Any(text => text.Contains("奖励依赖对应挑战或深渊资格")),"6081 覆盖缺口提示已去除");
        foreach (var other in legend.Where(quest => quest.Id != 6081).Take(2))
            That(!QuestCoverage.Scan(new(new() { [other.Id] = other }),items.Items,items.EpicPieces,parameters).Quests.Single().ImplementedPath,
                "传奇类专用奖励仍被拒绝 " + other.Id);
        var slotQuests = catalog.All.Where(quest => quest.Attributes.Any(value => value is "slot expansion" or "magic stone")).ToArray();
        That(slotQuests.Select(quest => quest.Id).Order().SequenceEqual([649,650,674,675,676,677,2635,2636,2711]) &&
            slotQuests.All(QuestCoverage.HasOrdinaryDeclarations),"九条真实槽位任务完整保留已确认声明，尾部任务不按固定六项遗漏");
        That(catalog.Get(675).Definition is { PrerequisiteGroups.Length: 0 } &&
            catalog.Get(675).Definition!.Values("pre required quest").Length == 0 && catalog.Get(675).Definition!.Has("pre required quest"),
            "真实魔法石前驱的空前置不被变成永久资格门禁");
        foreach (var quest in slotQuests)
        {
            var ready = new Character(1,0,"Slots",1,towns.Room,null) { Level = (byte)quest.MinimumLevel,
                CompletedQuests = quest.Prerequisites.ToArray() };
            for (byte job = 0; job < 16; job++)
            {
                var expected = ready with { Job = job };
                That(QuestEligibility.Evaluate(expected,quest,catalog).Visible &&
                    QuestEligibility.Evaluate(expected with { EquipmentSlotFlags = 0x13 },quest,catalog).Visible &&
                    !QuestEligibility.Evaluate(expected with { Level = (byte)(quest.MinimumLevel-1) },quest,catalog).Eligible,
                    "真实槽位链执行资源等级与16职业资格，不额外强加slot flag条件 " + quest.Id + "/" + job);
            }
            if (quest.Prerequisites.Count != 0)
                That(!QuestEligibility.Evaluate(ready with { CompletedQuests = [] },quest,catalog).Eligible,
                    "真实槽位后继仍要求完成相应前驱 " + quest.Id);
            if (quest.Condition != "[hunt monster]") continue;
            That(QuestHuntProgress.TryTargets(quest,out var targets),"真实前驱四元组进入共用权威猎杀模型 " + quest.Id);
            var active = new ActiveMainQuest(0,(ushort)quest.Id,QuestHuntProgress.Initial(quest)) { AcceptanceId = Guid.NewGuid() };
            var map = new DungeonMap("boss",new(0,0),76126,"map/fixture.map");
            var maze = new DungeonMaze(0,1,1,"fixture",[map.Position],[map.Position],[map],[]);
            var entry = new DungeonEntryPlan(1,1,0,(uint)quest.Id,0,2,maze,map,map) { QuestAcceptances = [active] };
            var after = active;
            foreach (var target in targets)
                after = QuestHuntProgress.MonsterDeath([after],entry with { DungeonId = (uint)target.DungeonId },catalog,
                    new(0,1,target.MonsterId,1,null))[0];
            That(after.ConditionState == 0 && after.AcceptanceId == active.AcceptanceId &&
                QuestHuntProgress.MonsterDeath([active with { AcceptanceId = Guid.NewGuid() }],
                    entry with { DungeonId = (uint)targets[0].DungeonId },catalog,
                    new(0,1,targets[0].MonsterId,1,null))[0].ConditionState == active.ConditionState,
                "真实前驱包括多地下城通道只由绑定接取的对应死亡推进，旧接取不能推进 " + quest.Id);
        }
        foreach (var quest in slotQuests.Where(quest => quest.RewardType == "[slot expansion]"))
        {
            byte level = (byte)quest.MinimumLevel;
            using var f = new QuestOrdinaryCompletionTests.Fixture(towns,characters,skills,growth,parameters,items,quest,
                materialCount: 0,definitions: slotQuests,active: [],useCycles: true,configure: candidate =>
                {
                    var learning = SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,level,
                        candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(level) },skills);
                    return candidate with { Level = level, CompletedQuests = quest.Prerequisites.ToArray(),
                        EquipmentSlotFlags = 0x20, InitialLearning = learning,
                        Skills = learning.Trees[0].Skills.Select(skill => new CharacterSkill(skill.Id,skill.Level)).OrderBy(skill => skill.Id).ToArray() };
                });
            var accepted = f.Change((ushort)quest.Id,QuestLifecycleAction.Accept);
            That(accepted?.Commit is { } accept && accept.Character.ActiveMainQuests.Single().AcceptanceId != Guid.Empty &&
                accept.Character.ActiveMainQuests.Single().ConditionState == 1,"真实寻物解锁从普通接取事务生成独立身份 " + quest.Id);
            var materials = Enumerable.Range(0,quest.IntData.Count/2).Select(at => new InventoryReward(quest.IntData[at*2],quest.IntData[at*2+1])).ToArray();
            That(f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,materials,
                items.Items,items.GoldLimits,items.EpicPieces)).Accepted && f.Read().ActiveMainQuests.Single().ConditionState == 0,
                "真实金币、晶体钱包与实体材料通过库存事务重算完成状态 " + quest.Id);
            var before = f.Read();
            var finished = f.Finish(ushort.MaxValue,(ushort)quest.Id);
            That(finished.Receipt?.Commit is { } commit && commit.Character.EquipmentSlotFlags == QuestCharacterReward.EquipmentSlots(before,quest) &&
                commit.Character.CompletedQuests.Contains(quest.Id) && commit.Character.ActiveMainQuests.Length == 0 &&
                commit.Consumed.Sum(row => (long)row.Count) == materials.Sum(row => (long)row.Count) &&
                materials.All(row => CurrencyWallet.RewardKind(row.ItemId) is { } kind ?
                    CurrencyWallet.Get(commit.Character.Inventory,kind) == (kind == CurrencyKind.Gold ? commit.GoldAmount : 0) :
                    commit.Character.Inventory.Items.All(item => item.Core.ItemId != row.ItemId)) && f.Receipts == 1,
                "真实三类解锁一次提交完成位、物品消费与槽flag，沿通用奖励经验逻辑 " + quest.Id);
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(f.Read().EquipmentSlotFlags == QuestCharacterReward.EquipmentSlots(before,quest) &&
                f.Finish(ushort.MaxValue,(ushort)quest.Id).Outcome == QuestFinishOutcome.AlreadyCompleted && f.Receipts == 1,
                "真实解锁重登恢复，重复提交不再消费或发奖 " + quest.Id);
        }
        foreach (var reward in new[] { (Index: 0,Bit: 1), (Index: 1,Bit: 2), (Index: 2,Bit: 16) })
        {
            var slot = empty with { Condition = "[seeking]", IntData = [0,100], RewardType = "[slot expansion]", Rewards = [new(0,reward.Index,null)] };
            using var f = Make(slot,configure: value => value with { EquipmentSlotFlags = 0x20,
                Inventory = value.Inventory with { Gold = 1000 } });
            var before = f.Read();var plan = f.Prepare(ushort.MaxValue).Plan!;
            That(QuestLifecyclePlan.HasMaterials(before,slot,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),"金币编号0按真实余额参与寻物目标");
            f.Sql("CREATE TRIGGER slot_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'slot rollback'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => f.Repository.CommitQuestCompletion(f.Owner,plan),"装备槽奖励末段失败");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()),"槽解锁、扣金币、清槽、完成位与回执全部回滚");
            f.Sql("DROP TRIGGER slot_abort");
            var commit = f.Finish(ushort.MaxValue).Receipt!.Commit;
            That(commit.Character.EquipmentSlotFlags == (0x20 | reward.Bit) && commit.Character.Inventory.Gold == 900 &&
                commit.Consumed is [{ Uid: 0, ItemId: 0, Count: 100 }] && commit.ChangedSlots.Contains(new(InventoryListType.Main,0)),
                "按本版位值解锁，保留其它标志并原子消费Gold虚拟槽");
            int itemId = reward.Index switch { 0 => 100341597, 1 => 2747519, _ => 100390252 };
            var template = items.Items.Get(itemId);
            var core = InventoryRewardGrantService.Create(template);
            var worn = new InventorySlot(InventoryListType.Equipment,template.EquipmentSlot);
            for (byte job = 0; job < 16; job++)
            {
                var character = commit.Character with { Job = job, Level = 95 };
                That(!InventoryMoveService.Allows(character with { EquipmentSlotFlags = 0 },worn,core,items.Items) &&
                    InventoryMoveService.Allows(character,worn,core,items.Items) == ((template.Jobs & (1 << job)) != 0) &&
                    !InventoryMoveService.Allows(character with { Level = 1 },worn,core,items.Items),
                    "解锁穿戴面向16职业，仍执行真实物品职业和等级限制");
            }
            var ack = QuestPackets.Finish(100,0,0,[new(0,100)],0,[]);
            That(ack.Length == 18 && ack[9] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(10)) == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(12)) == 100 && ack[16] == 0 && ack[17] == 0,
                "槽奖励沿普通空物品ACK，Gold材料编码本次扣除量");
            var player = TownPackets.Player(commit.Character,new CharacterStatComputer(characters).Compute(commit.Character));
            That(player[125] == (0x20 | reward.Bit),"Player属性块后发送实际持久装备槽标志");
            f.Session.ReturnToRoster();f.Session.Select(0);f.Session.MarkReady();
            That(f.Read().EquipmentSlotFlags == commit.Character.EquipmentSlotFlags && f.Receipts == 1 &&
                f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.AlreadyCompleted,"重登恢复解锁且不能再次扣费发奖");
        }
        Console.WriteLine($"PASS {Count-checks} 角色任务奖励、金币消费、回滚和重登断言");

        QuestOrdinaryCompletionTests.Fixture Make(StoryQuest quest,Func<NewCharacter,NewCharacter>? configure = null) =>
            new(towns,characters,skills,growth,parameters,items,quest,materialCount: 0,active: [new(0,100,0)],configure: configure);
    }
}
