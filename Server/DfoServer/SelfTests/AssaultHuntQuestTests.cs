using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 任务 6084「[强袭模式]特殊补给」：真实 PVF 下核对猎杀目标合同、强袭任务副本的暗影格鲁姆刷出，
// 以及接取后 C2S16 携带该任务号仍沿普通迷宫进入任意普通图。
internal static class AssaultHuntQuestTests
{
    const int QuestId = 6084, ShadowGrum = 107004021;
    static readonly int[] MissionDungeons = [7320, 7321, 7323, 7324, 7325, 7326];
    static readonly uint[] OrdinaryDungeons = [291100319, 291100308, 86, 3];

    public static void RunResources(PvfArchive pvf, QuestCatalog quests)
    {
        var quest = quests.Get(QuestId);
        That(QuestHuntProgress.TryTargets(quest, out var targets) && targets.Length == 1 &&
            targets[0] is { DungeonId: -1, MinimumDifficulty: -1, MonsterId: ShadowGrum, Count: 3, EnemyType: 1 },
            "6084 通过 TryTargets：任意副本/难度、击杀模板107004021 ×3");
        That(quest is { Grade: "[side]", Condition: "[hunt monster]", SubType: -1, MainQuest: null } &&
            QuestCoverage.HasOrdinaryDeclarations(quest) &&
            quest.Definition!.Dungeons.Select(row => row.DungeonId).SequenceEqual(MissionDungeons.Concat([7327])),
            "6084 为无父任务的支线猎杀，dungeon info 仅强袭任务图：" +
            string.Join(',', quest.Definition!.Dungeons.Select(row => row.DungeonId + ":" + row.Parameter)));
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        foreach (int id in MissionDungeons)
        {
            var definition = dungeons.Get(id);
            int count = 0;
            foreach (var maze in definition.Mazes)
                foreach (var map in maze.MapSpecification ?? [])
                    count += DungeonMonsterPlan.FromRoom(rooms.Get(map, requirePassiveDefinitions: false), 1, 100, definition.BasisLevel ?? 95)
                        .Monsters.Count(row => row.MonsterId == ShadowGrum && row.IsBlocking && row.CanReportDeath);
            That(count >= 1, "强袭任务副本 " + id + " 的计划里至少刷出1只可上报死亡的暗影格鲁姆（每次通关推进1/3），实际 " + count);
        }
        var active = new ActiveMainQuest(1, QuestId, QuestHuntProgress.Initial(quest)) { AcceptanceId = Guid.NewGuid() };
        var definitionForEntry = dungeons.Get(7320);
        var entryMaze = definitionForEntry.Mazes[0];
        var start = entryMaze.MapSpecification![0];
        var entry = new DungeonEntryPlan(1, 0, 7320, 0, 0, 0, entryMaze, start, start) { QuestAcceptances = [active] };
        var monster = new DungeonPlannedMonster(0, 200, ShadowGrum, 95, null);
        var state = new[] { active };
        for (int kill = 2; kill >= 0; kill--)
        {
            state = QuestHuntProgress.MonsterDeath(state, entry, quests, monster).ToArray();
            That(state[0].ConditionState == (uint)kill, "击杀暗影格鲁姆经 QuestHuntProgress 递减到 " + kill);
        }

        var character = new Character(1, 0, "AssaultHunt", 0, new(38, 1, 1, 1), null)
            { Level = 95, ActiveMainQuests = [active] };
        foreach (uint dungeon in OrdinaryDungeons)
        {
            That(DungeonEntryPlan.TryCreate(dungeons, character, dungeon, QuestId, 0, out var plan, out var reason,
                    quest: quest, quests: quests, draw: _ => 0) &&
                plan is { IsOrdinaryHunt: true, IsOrdinary: true, RouteQuestId: 0, QuestId: QuestId } &&
                plan.QuestAcceptanceId == active.AcceptanceId && plan.UsesFatigue,
                "6084 任意副本猎杀进入普通图：" + dungeon + "/" + reason);
            That(DungeonEntryPlan.TryCreate(dungeons, character, dungeon, 0, 0, out var ordinary, out _,
                    quests: quests, draw: _ => 0) &&
                ordinary is { QuestId: 0, IsOrdinary: true, IsOrdinaryHunt: false },
                "6084 活动时普通0入口保持原模式：" + dungeon);
        }
        foreach (int id in MissionDungeons)
        {
            var definition = dungeons.Get(id);
            byte difficulty = definition.TryGetDesignatedDifficulty(out byte designated) ? designated : (byte)0;
            That(DungeonEntryPlan.TryCreate(dungeons, character, (uint)id, QuestId, difficulty, out var plan, out var reason,
                    quest: quest, quests: quests, draw: _ => 0) &&
                plan is { IsOrdinaryHunt: true, RouteQuestId: 0, QuestId: QuestId } &&
                plan.QuestAcceptanceId == active.AcceptanceId,
                "6084 任意副本猎杀进入已授权强袭任务图：" + id + "/" + reason);
        }
        That(!DungeonEntryPlan.TryCreate(dungeons, character, 5000, QuestId, 0, out _, out _, quest: quest, quests: quests) &&
            !DungeonEntryPlan.TryCreate(dungeons, character, 7142, QuestId, 0, out _, out _, quest: quest, quests: quests),
            "任意副本猎杀不能进入修炼场或职业体验");
        var specified = quests.Get(2948);
        var specifiedActive = new ActiveMainQuest(1, 2948, QuestHuntProgress.Initial(specified)) { AcceptanceId = Guid.NewGuid() };
        var specifiedCharacter = character with { Level = 45, ActiveMainQuests = [specifiedActive] };
        That(!DungeonEntryPlan.TryCreate(dungeons, specifiedCharacter, 41, 2948, 0, out _, out _, quest: specified, quests: quests),
            "指定副本猎杀不能借任意副本合同进入未声明地图");
    }

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var quest = quests.Get(QuestId);
        var area = towns.Areas.Values.First(row => row.Level <= 95 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 291100319));
        var done = new HashSet<int>(area.Quests) { 6119 };
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        done.UnionWith(quest.Definition!.PrerequisiteGroups.SelectMany(group => group));
        done.UnionWith(area.Map(done).DungeonGates.Where(gate => gate.Conditions.Length == 0).SelectMany(gate => gate.Quests));
        var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(walk.X + walk.Width / 2)),
            checked((ushort)(walk.Y + walk.Height / 2)));
        using var fixture = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
            done.Order().ToArray(), position: position);
        That(fixture.Session.ChangeQuest(QuestId, QuestLifecycleAction.Accept, quests, items.Items, items.GoldLimits,
                items.EpicPieces)?.Commit is not null,
            "95级哈林角色按原生命周期接取6084");
        var before = fixture.Read();
        var accepted = before.ActiveMainQuests.Single(row => row.QuestId == QuestId);
        var opening = fixture.Session.EnterFormalDungeon(291100319, QuestId, 0, dungeons, rooms, draw: _ => 0);
        That(opening is not null && opening.Entry is { IsOrdinaryHunt: true, QuestId: QuestId, RouteQuestId: 0 } &&
            opening.Entry.QuestAcceptanceId == accepted.AcceptanceId &&
            fixture.Session.LastFormalDungeonRejection is null,
            "接取6084后搏击俱乐部会话入场走普通猎杀：" + fixture.Session.LastFormalDungeonRejection);
        That(fixture.Session.CompleteFormalDungeonLoading() is not null, "6084普通猎杀起点可加载");
        That(fixture.Session.ReturnFromFormalDungeon() is not null &&
            fixture.Read().ActiveMainQuests.Single(row => row.QuestId == QuestId).AcceptanceId == accepted.AcceptanceId,
            "6084普通猎杀回城保留接取身份");
        Console.WriteLine($"PASS {Count - checks} 6084任意副本猎杀哈林会话入场");
    }
}
