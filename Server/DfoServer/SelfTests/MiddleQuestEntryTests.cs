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
    internal sealed record Scenario(ushort Quest, uint Dungeon, int StartMap, int MapCount, int Boss,
        bool ScriptedBoss, int[] FriendlyStart, int[] FriendlyEnd)
    {
        public int TargetMap => StartMap+MapCount-1;
    }
    internal static IReadOnlyList<Scenario> Scenarios { get; } = Array.AsReadOnly(new Scenario[]
    {
        new(3950,311,76800,9,75112,false,[],[]),
        new(3961,311,76820,8,75112,false,[],[75109]),
        new(3978,312,76930,9,75100,true,[6512,56500],[]),
        new(3980,312,76950,9,75100,true,[6512],[74654]),
        new(3997,313,77090,10,74657,false,[],[16802]),
        new(4839,314,92537,9,74662,false,[],[])
    });

    // 复用整轮资源与既有角色/Session夹具；每个角色接取一次，两种入场共用原接取。
    internal static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = CharacterGrowthCatalog.Load(pvf,skills);
        var dungeons = DungeonCatalog.Load(pvf);
        var rooms = DungeonRoomCatalog.Load(pvf);
        foreach (var (dungeon,count) in new[] { (311,17),(312,12),(313,16),(314,11) })
        {
            var definition = dungeons.Get(dungeon);
            That(definition.Mazes.Count == count && definition.RawSections.Count(row => row.Tag == "beast dungeon") == 3,
                "地轨四图保留普通maze名册及三个独立Beast原始块："+dungeon);
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
            That(fixture.Session.ChangeQuest(scenario.Quest,QuestLifecycleAction.Accept,quests,
                items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "真实前置与等级沿原生命周期接取："+scenario.Quest);
            var accepted = fixture.Read().ActiveMainQuests.Single(row => row.QuestId == scenario.Quest);
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
        Console.WriteLine($"PASS {Count-checks} 地轨六条真实路线完整生成及显式/普通0入场断言（未验证dummy演出或实机）");
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
        That(request == 0 ? entry.ActiveQuestSlot == byte.MaxValue && entry.QuestAcceptanceId == Guid.Empty :
            entry.ActiveQuestSlot == accepted.Slot && entry.QuestAcceptanceId == accepted.AcceptanceId,
            "请求0的哨兵身份与真实活动接取分别保存："+scenario.Quest);
    }

    static DungeonMonsterPlan[] CompletePlans(DungeonEntryPlan entry, DungeonRoomCatalog rooms, Scenario scenario)
    {
        var maps = entry.Maze.MapSpecification!.Concat(entry.Maze.LayeredMaps).Concat(entry.Maze.ConditionalMaps).ToArray();
        That(maps.Select(map => map.Id).SequenceEqual(Enumerable.Range(scenario.StartMap,scenario.MapCount)),
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
        var boss = plans[^1].Monsters.Single(row => row.IsBoss);
        That(boss.MonsterId == scenario.Boss && boss.IsScriptedBoss == scenario.ScriptedBoss &&
            plans[0].Monsters.Where(row => row.IsFriendly).Select(row => row.MonsterId).SequenceEqual(scenario.FriendlyStart) &&
            plans[^1].Monsters.Where(row => row.IsFriendly).Select(row => row.MonsterId).SequenceEqual(scenario.FriendlyEnd) &&
            plans[^1].SpecialObjects.Count == 3,
            "取证的真实Boss/dummy、友方演员与三只特殊父对象均保留："+scenario.Quest);
        return plans.ToArray();
    }

    static (int Id,int Level,byte Type,int Team,bool Friendly,bool Blocking) Shape(DungeonPlannedMonster actor)
        => (actor.MonsterId,actor.CandidateLevel,actor.SpawnType,actor.Team,actor.IsFriendly,actor.IsBlocking);
}
