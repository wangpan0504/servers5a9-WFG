using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 任务 6084「[强袭模式]特殊补给」：真实 PVF 下核对猎杀目标合同、强袭任务副本的暗影格鲁姆刷出，以及经 QuestHuntProgress 的计数。
internal static class AssaultHuntQuestTests
{
    const int QuestId = 6084, ShadowGrum = 107004021;
    static readonly int[] MissionDungeons = [7320, 7321, 7323, 7324, 7325, 7326];

    public static void RunResources(PvfArchive pvf, QuestCatalog quests)
    {
        var quest = quests.Get(QuestId);
        That(QuestHuntProgress.TryTargets(quest, out var targets) && targets.Length == 1 &&
            targets[0] is { DungeonId: -1, MinimumDifficulty: -1, MonsterId: ShadowGrum, Count: 3, EnemyType: 1 },
            "6084 通过 TryTargets：任意副本/难度、击杀模板107004021 ×3");
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
    }
}
