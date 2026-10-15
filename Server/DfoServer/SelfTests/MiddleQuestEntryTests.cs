using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MiddleQuestEntryTests
{
    // 这里只保存已取证回归样本，不参与生产资格或限定可接任务。
    internal sealed record Scenario(ushort Quest, uint Dungeon, int[] MapIds, int TargetMap,
        (int Id,bool Scripted)[] Bosses, int[] FriendlyStart, int[] FriendlyEnd,
        (int Map,int[] Ids)[] SpecialObjects)
    {
        public int StartMap => MapIds[0];
    }
    internal static IReadOnlyList<Scenario> Scenarios { get; } = Array.AsReadOnly(new Scenario[]
    {
        new(3947,310,[76680,76681,76682,76683,76684,76685,76686,76687,76688],76688,
            [(74642,false),(75105,false)],[],[],[(76688,[109000301,109000301,109000301])]),
        new(3950,311,[76800,76801,76802,76803,76804,76805,76806,76807,76808],76808,
            [(75112,false)],[],[],[(76808,[109000301,109000301,109000301])]),
        new(3961,311,[76820,76821,76822,76823,76824,76825,76826,76827],76827,
            [(75112,false)],[],[75109],[(76827,[109000301,109000301,109000301])]),
        new(3962,311,[76830,76831,76832,76833,76834,76835,76836,76837,76838,76839,76849],76849,
            [(75099,true)],[],[75112],
            [(76830,[]),(76831,[]),(76832,[]),(76833,[]),(76834,[]),(76835,[]),
             (76836,[]),(76837,[]),(76838,[]),(76839,[]),(76849,[109000301,109000301,109000301])]),
        new(3978,312,[76930,76931,76932,76933,76934,76935,76936,76937,76938],76938,
            [(75100,true)],[6512,56500],[],[(76938,[109000301,109000301,109000301])]),
        new(3980,312,[76950,76951,76952,76953,76954,76955,76956,76957,76958],76958,
            [(75100,true)],[6512],[74654],[(76958,[109000301,109000301,109000301])]),
        new(3997,313,[77090,77091,77092,77093,77094,77095,77096,77097,77098,77099],77099,
            [(74657,false)],[],[16802],[(77099,[109000302,109000302,109000302])]),
        new(4839,314,[92537,92538,92539,92540,92541,92542,92543,92544,92545],92545,
            [(74662,false)],[],[],[(92545,[109000303,109000303,109000303])]),
        new(7956,311,[655658,655659,655660,655661,655662],655662,
            [(63821,true),(63768,false)],[],[109008615],
            [(655658,[]),(655659,[67095]),(655660,[]),(655661,[]),(655662,[])])
    });

    internal static IReadOnlyList<Scenario> NpcScenarios { get; } = Array.AsReadOnly(new Scenario[]
    {
        new(3628,78,[91751,91752,91753,91754,91755,91756,91758,91759,91757],91757,
            [(107000195,false)],[],[],[(91753,[10607]),(91754,[10607,10607]),(91755,[10607]),
                (91756,[10607,10607]),(91759,[10607]),(91757,[10607,10607])]),
        new(3639,72,[91831,91832,91833,91834,91835,91836,91837,91838,91839],91837,
            [(107000197,false),(70216,true)],[],[],[(91833,[10607]),(91834,[10607,10607]),
                (91835,[10607]),(91836,[10607,10607]),(91837,[10607,10607]),(91839,[10607])])
    });

    // 复用整轮资源与既有角色/Session夹具；每个角色接取一次，两种入场共用原接取。
    internal static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = CharacterGrowthCatalog.Load(pvf,skills);
        var dungeons = DungeonCatalog.Load(pvf);
        var rooms = DungeonRoomCatalog.Load(pvf);
        foreach (var (scenario,template,parameter,monsterCount,passiveCount) in new[]
        {
            (NpcScenarios[0],107000195,1020,60,325), (NpcScenarios[1],107000197,1069,55,287)
        })
        {
            var dungeon = dungeons.Get(checked((int)scenario.Dungeon));
            var maze = dungeon.Mazes.Single(row => row.ConnectsQuest(scenario.Quest));
            var plans = maze.MapSpecification!.Select(map => DungeonMonsterPlan.Create(rooms,map,99,100,dungeon.BasisLevel,0)).ToArray();
            var target = plans.Single(plan => plan.Room.Map.Id == scenario.TargetMap);
            var npc = target.Monsters.Single(actor => actor.MonsterId == template);
            That(plans.Select(plan => plan.Room.Map.Id).SequenceEqual(scenario.MapIds) && plans.Length == 9 &&
                plans.Sum(plan => plan.Monsters.Count) == monsterCount && plans.Sum(plan => plan.Room.PassiveObjects.Count) == passiveCount &&
                plans.Sum(plan => plan.SpecialObjects.Count) == 9, "NPC路线保留完整九房、原怪物及全部普通/特殊物件");
            That(npc is { SourceOrdinal: 0, SpawnType: 3, CandidateLevel: 80, Team: 100, IsFriendly: false,
                    IsBlocking: true, CanReportDeath: true, IsBoss: true, IsScriptedBoss: false } &&
                npc.Source is { Modifiers: ["[NPC]"] } source && source.NpcParameter == parameter &&
                ReferenceEquals(source,target.Room.Monsters[0]), "实际NPC参数与原monster源行绑定，保留默认阵营和普通Boss品级");
            That(plans.SelectMany(plan => plan.Monsters).Count(actor => actor.Source?.NpcParameter is not null) == 1 &&
                (scenario.Quest == 3628
                    ? plans.SelectMany(plan => plan.Monsters).Count(actor => actor.Team == 50 && !actor.IsFriendly && actor.IsBlocking) == 10
                    : target.Monsters.Single(actor => actor.MonsterId == 70216) is { IsScriptedBoss: true, SpawnType: 3 }),
                "乌塔拉team50与无法地带独立dummy70216各保留原合同，不由NPC标记推断友方或脚本退场");
        }
        foreach (var (dungeon,count) in new[] { (310,12),(311,17),(312,12),(313,16),(314,11) })
        {
            var definition = dungeons.Get(dungeon);
            That(definition.Mazes.Count == count && definition.RawSections.Count(row => row.Tag == "beast dungeon") == 3,
                "地轨五图保留普通maze名册及三个独立Beast原始块："+dungeon);
        }
        foreach (var scenario in Scenarios)
        {
            var quest = quests.Get(scenario.Quest);
            That(QuestClearEligibility.IsSingleMap(quest) && quest.IntData[0] == scenario.TargetMap &&
                quest.Definition!.Dungeons.Any(row => row.DungeonId == scenario.Dungeon),
                "地轨任务回归使用真实清图目标与所属副本："+scenario.Quest);
            var entrance = Entrance(towns,quest,scenario.Dungeon);
            using var fixture = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,
                checked((byte)quest.MinimumLevel),entrance.Completed,position:entrance.Position);
            if (scenario.Quest is 3962 or 7956)
            {
                bool mission = scenario.Quest == 7956;
                That(quest.MinimumLevel == (mission ? 95 : 88) && quest.MaximumLevel == 99 &&
                    quest.Grade == (mission ? "[mission]" : "[epic]") &&
                    quest.Prerequisites.SequenceEqual(new[] { mission ? 6045 : 3961 }) &&
                    QuestEligibility.InspectResource(quest).Entry == QuestResourceEntry.Ordinary &&
                    QuestCoverage.HasOrdinaryDeclarations(quest),
                    "新增样本保留真实分类、等级及前置，符合原普通接取声明："+scenario.Quest);
                if (mission)
                {
                    var eligible = fixture.Read();
                    foreach (var denied in new[] { eligible with { Level = 94 },
                        eligible with { CompletedQuests = [.. eligible.CompletedQuests.Where(id => id != 6045)] } })
                    {
                        That(!QuestEligibility.Evaluate(denied,quest,quests).Eligible,
                            "7956拒绝94级或缺少6045前置的角色");
                        Reject<InvalidDataException>(() => QuestLifecyclePlan.Accept(denied,quest,quests,
                            items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow),
                            "7956不能绕过真实等级或前置进入接取规划");
                    }
                }
            }
            That(fixture.Session.ChangeQuest(scenario.Quest,QuestLifecycleAction.Accept,quests,
                items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "真实前置与等级沿原生命周期接取："+scenario.Quest);
            var accepted = fixture.Read().ActiveMainQuests.Single(row => row.QuestId == scenario.Quest);
            if (scenario.Quest == 3947)
            {
                That(quest.MinimumLevel == 88 && quest.MaximumLevel == 99 && quest.Prerequisites.SequenceEqual(new[] { 3946 }) &&
                    dungeons.Get(310).Mazes.Single(maze => maze.ConnectsQuest(3947)).SourceOrdinal == 2,
                    "小魔女斯库尔蒂保留真实等级、前置及第二条任务迷宫，不借用3946路线");
                // 邻接任务的目标虽存在于同一副本，也不能替代本任务迷宫中的真实清图地图。
                That(!DungeonEntryPlan.TryCreate(dungeons,fixture.Read(),310,3947,0,out var rejected,out var rejectedReason,
                    quest:quest with { IntData = [76678] },quests:quests) && rejected is null &&
                    rejectedReason == "quest-terminal-map-unavailable",
                    "3947拒绝借用3946的清图目标，不能回退到相邻任务迷宫");
            }
            if (scenario.Quest == 3962)
            {
                That(!DungeonEntryPlan.TryCreate(dungeons,fixture.Read(),311,3962,0,out var rejected,out var rejectedReason,
                    quest:quest with { IntData = [76848] },quests:quests) && rejected is null &&
                    rejectedReason == "quest-terminal-map-unavailable",
                    "3962拒绝相邻3963的76848目标，不能以连续编号补出不存在的本任务地图");
            }
            DungeonEntryPlan? first = null;
            DungeonMonsterPlan[]? fullPlans = null;
            Guid previousInstance = Guid.Empty;
            foreach (uint request in new uint[] { scenario.Quest,0 })
            {
                var before = fixture.Read();
                That(DungeonEntryPlan.TryCreate(dungeons,before,scenario.Dungeon,request,0,out var plan,out var reason,
                    quest:request == 0 ? null : quest,quests:quests),
                    $"地轨实际路线规划失败：quest={scenario.Quest} request={request} reason={reason}");
                var entry = plan!;
                CheckBinding(entry,before,accepted,scenario,request);
                That(entry.CharacterRevision == before.Revision,"只读规划冻结原角色revision："+scenario.Quest);
                if (first is null)
                {
                    first = entry;
                    fullPlans = CompletePlans(entry,rooms,scenario);
                }
                else
                    That(entry.Maze.SameLayout(first.Maze) && entry.StartMap == first.StartMap && entry.BossMap == first.BossMap,
                        "普通0复用本角色唯一活动路线，未落入普通或Beast布局："+scenario.Quest);
                var authority = new NativeDungeonCombatAuthority();
                var route = new DungeonFixedRoute(fullPlans!,entry.Maze.StaticTopology.Links!
                    .Select(link => new DungeonRouteConnection(link.First,link.Second)).ToArray());
                That(SinglePlayerDungeonRun.TryCreate(entry,1,100,route,authority,out var run,out var runReason) &&
                    authority.BindTo(run!) && run!.Snapshot.Identity.QuestId == request,
                    "完整资源与全部演员能建立原正式Run："+scenario.Quest+" "+runReason);
                var opening = fixture.Session.EnterFormalDungeon(scenario.Dungeon,request,0,dungeons,rooms);
                That(opening is not null,"完整生产Session入场不得删除后续演员或替换地图："+scenario.Quest);
                var opened = opening!;
                CheckBinding(opened.Entry,before,accepted,scenario,request);
                That(opened.Entry.CharacterRevision == before.Revision+1 && opened.Generation == fixture.Owner.Generation &&
                    opened.Snapshot.Identity.QuestId == request &&
                    opened.Snapshot.Identity.CharacterId == before.Id && opened.Snapshot.Identity.InstanceId != previousInstance &&
                    opened.Snapshot.Identity.TaskAttempt == before.DungeonState!.LastAttempt+1 &&
                    fixture.Session.CanPublishFormalDungeon(opened),
                    "两种请求各建立一次本人owner的独立实际尝试："+scenario.Quest);
                previousInstance = opened.Snapshot.Identity.InstanceId;
                var loaded = fixture.Session.CompleteFormalDungeonLoading();
                That(loaded is not null && fixture.Session.Phase == SessionPhase.Dungeon &&
                    loaded.Monsters.Monsters.Select(Shape).SequenceEqual(fullPlans![0].Monsters.Select(Shape)) &&
                    fixture.Session.EnableFormalDungeonSettlement() is null,
                    "真实起点完成加载并保留全部演员；入场不许可结算："+scenario.Quest);
                That(fixture.Session.ReturnFromFormalDungeon() is not null && !fixture.Session.CanPublishFormalDungeon(opened),
                    "未通关返回释放原实例，不能发布旧入场回执："+scenario.Quest);
                var after = fixture.Read();
                That(after.ActiveMainQuests.SequenceEqual(before.ActiveMainQuests) &&
                    after.CompletedQuests.SequenceEqual(before.CompletedQuests) &&
                    JsonSerializer.Serialize(after.Inventory) == JsonSerializer.Serialize(before.Inventory) &&
                    JsonSerializer.Serialize(after.Learning) == JsonSerializer.Serialize(before.Learning) &&
                    fixture.Repository.LoadQuestClear(fixture.Owner,after,scenario.Quest) is null,
                    "入场/加载/退出不制造猎杀、清图、完成位或奖励："+scenario.Quest);
            }
            That(!QuestHuntProgress.Bound(accepted with { AcceptanceId=Guid.NewGuid() },first!),
                "重接的新身份不能继承旧路线绑定："+scenario.Quest);
        }
        Console.WriteLine($"PASS {Count-checks} 地轨九条真实路线完整生成及显式/普通0入场断言（未验证dummy演出或实机）");
    }

    internal static (Location Position,int[] Completed) Entrance(TownCatalog towns, StoryQuest quest, uint dungeon)
    {
        var area = towns.Areas.Values.First(row => row.Level <= quest.MinimumLevel && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeon));
        var done = new HashSet<int>(quest.Prerequisites);
        done.UnionWith(area.Quests);
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        return (new(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2))),
            done.Order().ToArray());
    }

    static void CheckBinding(DungeonEntryPlan entry, Character character, ActiveMainQuest accepted, Scenario scenario, uint request)
    {
        That(entry.CharacterId == character.Id && entry.DungeonId == scenario.Dungeon && entry.QuestId == request &&
            entry.RouteQuestId == scenario.Quest && !entry.IsOrdinary && entry.HasExplicitClearMap &&
            entry.StartMap.Id == scenario.StartMap && entry.BossMap.Id == scenario.TargetMap &&
            entry.QuestAcceptances.SequenceEqual(character.ActiveMainQuests) && QuestHuntProgress.Bound(accepted,entry),
            "实际地图、RouteQuest及原槽位/任务/Acceptance完整冻结："+scenario.Quest);
        if (scenario.Quest is 3962 or 7956)
            That(entry.Maze.SourceOrdinal == (scenario.Quest == 3962 ? 4 : 14) &&
                entry.BaseBossMap == entry.BossMap && entry.QuestClearMap is null && entry.TerminalLayerMap is null,
                "新增样本保持原任务迷宫、基础Boss与显式末图，无中途或末层替换："+scenario.Quest);
        That(request == 0 ? entry.ActiveQuestSlot == byte.MaxValue && entry.QuestAcceptanceId == Guid.Empty :
            entry.ActiveQuestSlot == accepted.Slot && entry.QuestAcceptanceId == accepted.AcceptanceId,
            "请求0的哨兵身份与真实活动接取分别保存："+scenario.Quest);
    }

    static DungeonMonsterPlan[] CompletePlans(DungeonEntryPlan entry, DungeonRoomCatalog rooms, Scenario scenario)
    {
        var maps = entry.Maze.MapSpecification!.Concat(entry.Maze.LayeredMaps).Concat(entry.Maze.ConditionalMaps).ToArray();
        That(maps.Select(map => map.Id).SequenceEqual(scenario.MapIds),
            "地轨代表任务完整MAP名册与取证范围一致："+scenario.Quest);
        var plans = new List<DungeonMonsterPlan>();
        var keys = new HashSet<ushort>();
        ushort next = 100;
        foreach (var map in maps)
        {
            var room = rooms.Get(map,requirePassiveDefinitions:false);
            var plan = DungeonMonsterPlan.FromRoom(room,entry.CharacterId,next,entry.BasisLevel,playerTeam:entry.PlayerTeam);
            That(plan.Monsters.Count(row => row.Source is { SpecialChild:null }) == room.Monsters.Count &&
                plan.Monsters.Count(row => row.AiCharacter is not null) == room.AiCharacters.Count &&
                plan.SpecialObjects.Count == room.SpecialObjects.Count &&
                plan.Monsters.Select(row => row.ObjectKey).Concat(plan.SpecialObjects.Select(row => row.ObjectKey))
                    .All(key => key != entry.CharacterId && keys.Add(key)),
                $"完整生成保留普通怪物/APC/特殊父子身份及全实例唯一key：quest={scenario.Quest} map={map.Id}");
            plans.Add(plan);
            if (plan.Monsters.Count != 0 || plan.SpecialObjects.Count != 0)
                next = checked((ushort)plan.NextObjectKey);
        }
        var bosses = plans[^1].Monsters.Where(row => row.IsBoss).ToArray();
        That(bosses.Select(row => (row.MonsterId,row.IsScriptedBoss)).SequenceEqual(scenario.Bosses) &&
            plans[0].Monsters.Where(row => row.IsFriendly).Select(row => row.MonsterId).SequenceEqual(scenario.FriendlyStart) &&
            plans[^1].Monsters.Where(row => row.IsFriendly).Select(row => row.MonsterId).SequenceEqual(scenario.FriendlyEnd),
            "取证的每只Boss/dummy标记及起末图友方演员均保留："+scenario.Quest);
        foreach (var (map,ids) in scenario.SpecialObjects)
            That(plans.Single(plan => plan.Room.Map.Id == map).SpecialObjects.Select(row => row.Source.ObjectId).SequenceEqual(ids),
                $"特殊父对象逐图保留准确模板、顺序与数量：quest={scenario.Quest} map={map}");
        if (scenario.Quest == 7956)
            That(plans[^1].Monsters.Count(row => row.MonsterId == 63768 && !row.IsBoss && row.SpawnType == 0 &&
                    row.Team == 100 && row.IsBlocking) == 1 &&
                plans[^1].Monsters.Single(row => row.MonsterId == 109008615) is
                    { AiCharacter: null, Team: 0, IsFriendly: true, IsBlocking: false },
                "7956同模板普通63768不成为第三Boss，友方109008615保留monster而非APC身份");
        return plans.ToArray();
    }

    static (int Id,int Level,byte Type,int Team,bool Friendly,bool Blocking) Shape(DungeonPlannedMonster actor)
        => (actor.MonsterId,actor.CandidateLevel,actor.SpawnType,actor.Team,actor.IsFriendly,actor.IsBlocking);
}
