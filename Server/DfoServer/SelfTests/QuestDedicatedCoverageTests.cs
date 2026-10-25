using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestDedicatedCoverageTests
{
    public static void Run(PvfArchive pvf,ItemResources items)
    {
        int before = Count;
        var quests = SelfTestCatalogs.Quests(pvf);
        var connections = SelfTestCatalogs.QuestConnections(pvf);
        var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
        var original = QuestCoverage.Scan(quests,items.Items,items.EpicPieces,parameters);
        var covered = QuestCoverage.Scan(quests,items.Items,items.EpicPieces,parameters,connections:connections);
        That(!original.DedicatedPathsChecked && covered.DedicatedPathsChecked,"未提供专用目录时不冒充已经检查");
        foreach (var (question,marker) in new[] { (6217,6218),(6382,6315),(6383,6380) })
        {
            foreach (int id in new[] { question,marker })
            {
                That(!original.Quests.Single(row => row.Id == id).ImplementedPath &&
                    covered.Quests.Single(row => row.Id == id) is { ImplementedPath:true,Restrictions.Length:0,SupportedDedicatedPaths.Length:>0 },
                    $"{id}有实际专用入口，不再误报为普通接取功能缺失");
            }
            That(QuestEligibility.InspectResource(quests.Get(marker)).Entry == QuestResourceEntry.Dedicated &&
                !QuestCoverage.HasSupportedTarget(quests.Get(marker),quests),"无NPC标记没有因此开放普通接取和虚构会面目标");
            var mutated = quests.All.ToDictionary(row => row.Id);
            mutated[marker] = quests.Get(marker) with { Rewards = [new(0,2058,null),new(0,1,null)] };
            var invalid = QuestDedicatedCoverage.Inspect(new(mutated),connections);
            That(!invalid.ContainsKey(question) && !invalid.ContainsKey(marker),"带普通奖励的marker不能冒用无奖励专用合同");
            mutated = quests.All.ToDictionary(row => row.Id);
            connections.TryGetQuestion(question,out var unit);
            mutated.Remove(unit.QuickClear!.Value.EndQuestId);
            invalid = QuestDedicatedCoverage.Inspect(new(mutated),connections);
            That(!invalid.ContainsKey(question) && !invalid.ContainsKey(marker),"真实范围终点缺失时不把专用路径标成可用");
        }
        That(covered.ImplementedPaths == original.ImplementedPaths+6,"本批只纠正已验证的三问答和三账号标记，不泛开其余专用分类");
        That(QuestDedicatedCoverage.Inspect(quests,null).Count == 0 &&
            QuestDedicatedCoverage.Inspect(quests,QuestConnectionCatalog.Parse([], _=>"")).Count == 0,
            "无连接目录或空目录不猜测旅程端点");
        var hope = OperationHopeCatalog.Load(pvf,SelfTestCatalogs.Dungeons(pvf),quests,SelfTestCatalogs.Rooms(pvf));
        var withHope = QuestCoverage.Scan(quests,items.Items,items.EpicPieces,parameters,connections:connections,hope:hope);
        That(!covered.HopePathsChecked && withHope.HopePathsChecked, "未显式装配希望领域时不冒称检查过实例路径");
        foreach (int id in hope.MissionIds)
        {
            That(!covered.Quests.Single(row => row.Id == id).ImplementedPath &&
                withHope.Quests.Single(row => row.Id == id) is { ImplementedPath:true,Restrictions.Length:0,SupportedDedicatedPaths.Length:1 },
                "全表Scan只在显式装配希望目录后计入九条完整专用路径");
            That(withHope.Quests.Single(row => row.Id == id).Entry == QuestResourceEntry.Dedicated,
                "Scan实现标志不能把实例入口改写成普通接取");
        }
        That(withHope.ImplementedPaths == covered.ImplementedPaths+9,"完整Scan希望差量精确为九任务");
        var instances = QuestDedicatedCoverage.Inspect(quests,null,hope);
        That(instances.Count == 9, "仅显式装配的九个希望实例任务进入专用覆盖");
        foreach (int id in hope.MissionIds)
        {
            That(instances[id] is { InstanceMission:true,AccountMarker:false,Answers.IsEmpty:true } &&
                instances[id].Paths.SequenceEqual(new[] { "operation-hope:1598/16/8523" }), "希望路径不能混为账号marker或NPC问答");
            That(QuestEligibility.InspectResource(quests.Get(id)).Entry == QuestResourceEntry.Dedicated &&
                !QuestCoverage.HasSupportedTarget(quests.Get(id),quests), "标为专用已实现不能放开普通接取/猎杀");
        }
        var altered = quests.All.ToDictionary(row => row.Id);
        altered[6171] = quests.Get(6171) with { Rewards = [new(0,2058,null),new(0,1,null)] };
        That(!QuestDedicatedCoverage.Inspect(new(altered),null,hope).ContainsKey(6171), "旧希望目录不能替新任务快照证明完整奖励合同");
        That(QuestDedicatedCoverage.Inspect(quests,connections,hope).Count == 15, "希望九实例和旅程六条专用路径独立合并");
        Console.WriteLine($"PASS {Count-before} 专用旅程与账号marker覆盖普查断言");
    }
}
