using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestNpcMappingTests
{
    public static void RunCore()
    {
        int checks = Count;
        var quest = Read([new(0,607),new(0,611),new(6,5),new(0,101)],
            [new(0,607),new(0,100000175),new(6,5),new(0,102)],
            [new(0,611),new(0,100000175),new(6,5),new(0,102)]);
        That(quest.Definition is { Diagnostics.Length: 0 } && quest.Definition.AlternativeNpcs.SequenceEqual(
            [new(607,611,101),new(607,100000175,102),new(611,100000175,102)]),
            "重复替代NPC声明按原序保存，完整大编号不截断");
        var character = new Character(1,0,"NpcMapping",1,new(1,0,50,50),null);
        That(QuestClientSceneProgress.ResolveNpc(character,607,quest) == 607,
            "未完成条件仍使用原NPC，不把替代条件当任务前置");
        That(QuestClientSceneProgress.ResolveNpc(character with { CompletedQuests = [101] },607,quest) == 611 &&
            QuestClientSceneProgress.ResolveNpc(character with { CompletedQuests = [101,102] },607,quest) == 100000175,
            "同一原NPC的多条成立规则使用最后命中目标");
        That(QuestClientSceneProgress.ResolveNpc(character with { AccountCompletedQuests = [102] },611,quest) == 100000175 &&
            QuestClientSceneProgress.ResolveNpc(character with { CompletedQuests = [101], AccountCompletedQuests = [102] },607,quest) == 100000175,
            "替代条件消费356投影共用的角色和账号完成集合");
        var chain = Read([new(0,607),new(0,611),new(6,5),new(0,101)],
            [new(0,611),new(0,777),new(6,5),new(0,101)]);
        That(QuestClientSceneProgress.ResolveNpc(character with { CompletedQuests = [101] },607,chain) == 611,
            "每行只匹配原始NPC，607替代611后不继续级联777");
        That(QuestClientSceneProgress.ResolveNpc(character,999,quest) == 999 &&
            QuestClientSceneProgress.ResolveNpc(character,607,quest with { Definition = null }) == 607,
            "无对应规则或无定义时保留原NPC");
        That(QuestClientSceneProgress.ResolveNpc(character with { ActiveMainQuests = [new(0,102,0)] },607,quest) == 607,
            "活动或进度归零尚未交付的任务不能触发已完成条件");
        foreach (PvfToken[] bad in new PvfToken[][]
        {
            [new(0,607),new(0,611)],
            [new(0,607),new(0,611),new(6,5)],
            [new(0,607),new(0,611),new(6,6),new(0,101)],
            [new(0,607),new(0,611),new(0,5),new(0,101)],
            [new(0,607),new(0,611),new(6,5),new(0,101),new(0,102)]
        })
        {
            var invalid = Read(bad);
            That(invalid.Definition is { Diagnostics.Length: > 0, AlternativeNpcs.Length: 0 } &&
                invalid.Definition.Has("alternative npc index") &&
                QuestEligibility.InspectResource(invalid).Entry == QuestResourceEntry.Invalid,
                "未确认替代形状保留原文并以Diagnostics拒绝资格");
        }

        var point = character.Position;
        var before = new TownMap("fixture/npc-before.map", [new(new(0,0,200,200),[],[])],[],false,[new(607,50,50),new(611,60,50)]);
        var after = before with { Path = "fixture/npc-after.map", Npcs = [new(100000175,70,50)] };
        var area = new TownArea(1,0,1,[],[],[],point,before,[after],[new(0,102)]);
        var towns = new TownCatalog(new() { [(1,0)] = area },point,point,new(0,[],[]),[],new Dictionary<uint,uint[]>());
        var catalog = new QuestCatalog(new() { [quest.Id] = quest });
        That(QuestClientSceneProgress.DialogueNpc(character,607,quest,catalog,towns) &&
            QuestClientSceneProgress.DialogueNpc(character with { CompletedQuests = [101] },607,quest,catalog,towns),
            "当前真实TownCatalog场景按原始或已触发的替代NPC验证");
        var shifted = character with { AccountCompletedQuests = [102] };
        That(QuestClientSceneProgress.DialogueNpc(shifted,611,quest,catalog,towns),
            "账号完成造成地图phase切换后，原交付NPC匹配大编号替代实体");
        That(!QuestClientSceneProgress.DialogueNpc(shifted with { Position = new(1,1,50,50) },611,quest,catalog,towns) &&
            !QuestClientSceneProgress.DialogueNpc(shifted,999,quest,catalog,towns),
            "替代映射不豁免实际场景或任意其它NPC");

        var previous = QuestEligibilityTests.Quest(201) with
        {
            Condition = "[look cinematic]", IntData = [1], CinematicPath = "fixture/actor.ani",
            Definition = new([])
            {
                Fields = [new("auto accept",[new(6,0,"enter town"),new(0,1,null)],[]),
                    new("go area guide",[new(0,1,null),new(0,0,null),new(0,50,null),new(0,50,null),new(6,0,"guide")],[]),
                    new("solve message",[new(6,0,"<npc::100000175>Actor")],[])]
            }
        };
        var actorQuest = quest with { Definition = quest.Definition! with { PrerequisiteGroups = [[201]] } };
        catalog = new(new() { [quest.Id] = actorQuest, [201] = previous });
        var emptyTown = new TownCatalog(new() { [(1,0)] = area with { Default = before with { Npcs = [] }, Phases = [] } },
            point,point,new(0,[],[]),[],new Dictionary<uint,uint[]>());
        var inActorScene = character with { CompletedQuests = [102,201] };
        That(QuestClientSceneProgress.DialogueNpc(inActorScene,611,actorQuest,catalog,emptyTown),
            "无常驻实体时，已完成直接前置演出的同场景临时演员使用替代编号");
        That(!QuestClientSceneProgress.DialogueNpc(character with { CompletedQuests = [102] },611,actorQuest,catalog,emptyTown),
            "未完成前置演出不能凭替代编号虚构临时演员");
        Console.WriteLine($"PASS {Count-checks} 替代NPC解析、完成条件、顺序映射与场景断言");
    }

    public static void RunResources(TownCatalog towns, QuestCatalog catalog)
    {
        int checks = Count;
        var mapped = catalog.All.Where(quest => quest.Definition is { Diagnostics.Length: 0 } definition &&
            definition.Has("alternative npc index")).ToArray();
        That(mapped.Length > 0 && mapped.All(quest => quest.Definition is { Diagnostics.Length: 0 } &&
            quest.Definition.AlternativeNpcs.Length == quest.Definition.Fields.Count(field => field.Name == "alternative npc index") &&
            QuestCoverage.HasOrdinaryDisplay(quest)),"匹配资源中的替代规则全部保留原声明及完整解析行");
        var ids = mapped.SelectMany(quest => quest.Definition!.AlternativeNpcs.SelectMany(row => new[] { row.Source,row.Target }))
            .Where(id => id >= 0).Select(id => (uint)id).ToHashSet();
        var conditions = mapped.SelectMany(quest => quest.Definition!.AlternativeNpcs.Select(row => row.CompletedQuest)).Distinct().Order().ToArray();
        // 这里只核对共享只读地图来源，不接取、推进或交付任何实际任务。
        foreach (var completed in new[] { Array.Empty<int>() }.Concat(conditions.Select(id => new[] { id })).Append(conditions))
        {
            var state = completed.ToHashSet();
            Console.WriteLine("NPC mapping source " + JsonSerializer.Serialize(new
            {
                Completed = completed,
                Maps = towns.Areas.Values.Select(area => new
                {
                    area.Town, area.Area, Path = area.Map(state).Path,
                    Npcs = area.Map(state).Npcs.Where(npc => ids.Contains(npc.Id)).Select(npc => npc.Id).ToArray()
                }).Where(area => area.Npcs.Length != 0).ToArray()
            }));
        }
        Console.WriteLine($"PASS {Count-checks} 替代NPC真实声明及只读地图来源断言");
    }

    static StoryQuest Read(params PvfToken[][] rows)
    {
        var strings = new Dictionary<int,string>
        {
            [1] = "[level]", [2] = "[type]", [3] = "[meet npc]", [4] = "[alternative npc index]",
            [5] = "[cleared quest]", [6] = "[accept quest]"
        };
        var tokens = new List<PvfToken> { new(3,1),new(0,1),new(0,99),new(3,2),new(6,3) };
        foreach (var row in rows) { tokens.Add(new(3,4)); tokens.AddRange(row); }
        return QuestDefinitionReader.Read(100,"fixture/npc-mapping.qst",tokens,id => strings[id]);
    }
}
