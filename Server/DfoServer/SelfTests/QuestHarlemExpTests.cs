using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
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
        // 目标侧限制不因本批声明确认而放宽：6060的-2选择符、6053/6056/6059的seeking子类型0仍未接入。
        foreach (int id in new[] { 6053, 6056, 6059, 6060 })
            That(!QuestCoverage.HasSupportedTarget(catalog.Get(id),catalog),"目标限制仍保留：" + id);
        // 其余已知带这两声明的任务只有这批；其它任务的未确认声明不受影响。
        That(catalog.All.Where(quest => quest.Definition?.Has("daily random set") == true || quest.Definition?.Has("next relation quest") == true)
            .Select(quest => quest.Id).Order().SequenceEqual(Group),"资源中带这两个声明的只有6051–6060");
        Console.WriteLine($"PASS {Count - checks} 黑市EXP 6051–6060真实资源接取资格断言");
    }
}