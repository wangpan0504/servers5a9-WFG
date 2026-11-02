using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 只调整本次普通PVE实际难度，不改变QST进度判定、模式授权或其它成员的个人任务。
internal static class QuestDungeonDifficulty
{
    public static byte Resolve(Character character, DungeonDefinition dungeon, uint requestedQuest,
        StoryQuest? requested, QuestCatalog? catalog, byte difficulty)
    {
        if (difficulty > 4 || dungeon.DungeonType is not null || dungeon.IsRaid || dungeon.IsTutorial || dungeon.IsClassExperience ||
            dungeon.IsTournament || dungeon.RequiresGriefEntryContract || dungeon.IsInfernoDungeon || dungeon.RequiresCosmoEntryContract) return difficulty;
        var targets = new List<StoryQuest>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (requestedQuest != 0 && requested is not null) Add(requested);
        if (catalog is not null)
            foreach (var active in character.ActiveMainQuests.Where(row => row.AcceptanceId != Guid.Empty && row.ConditionState != 0))
                if (catalog.TryGet(active.QuestId, out var quest)) Add(quest);
        var minima = new List<int>();
        foreach (var quest in targets)
        {
            if (QuestHuntProgress.TryTargets(quest, out var hunt))
                minima.AddRange(hunt.Where(row => Related(quest, row.Scope ?? new(
                    row.DungeonId == -1 ? QuestDungeonSelectorKind.Any : QuestDungeonSelectorKind.Dungeon, row.DungeonId, row.MinimumDifficulty)))
                    .Select(row => row.MinimumDifficulty));
            var parameters = QuestTargetSchema.Read(quest);
            if (QuestDungeonClearProgress.Supports(quest) && parameters.State == QuestParameterState.Parsed &&
                (quest.SubType != 17 || DungeonLuckyStarPolicy.IsRecommendedLevel(dungeon, character.Level)))
            {
                var options = parameters.Dungeons.Where(row => Related(quest, row)).Select(row => row.Difficulty).ToArray();
                if (options.Length != 0) minima.Add(options.Min());
            }
            if (quest.TargetKind == QuestTargetKind.Seeking && quest.SubType == 20 && QuestSeekingObjective.TryRead(quest, out var seeking) &&
                seeking.MinimumClearDifficulty is >= 0 && DungeonLuckyStarPolicy.IsRecommendedLevel(dungeon, character.Level))
                minima.Add(seeking.MinimumClearDifficulty.Value);
            if (quest.Definition is not { } definition) continue;
            // 同物品多掉落行是OR：取可获取该物品的最低门槛；不同任务/材料再取最高门槛。
            foreach (var rows in definition.Drops.Where(row => row.DungeonId == -1 || row.DungeonId == dungeon.Id).GroupBy(row => row.ItemId))
                if (rows.Min(row => row.Difficulty) is >= 0 and var required) minima.Add(required);
        }
        var connections = dungeon.Mazes.Where(maze => targets.Any(quest => maze.HasActiveQuestConnection(quest.Id) ||
            quest.MainQuest is > 0 && maze.HasActiveQuestConnection(quest.MainQuest.Value))).ToArray();
        // 普通指定档不受任务改写；剧情块有自己的范围，不能被块外普通指定档覆盖。
        if (connections.Length == 0 && (dungeon.HasDesignatedDifficultyConstraint || minima.Count == 0 || minima.Max() <= difficulty)) return difficulty;
        int minimum = Math.Max(difficulty, minima.Count == 0 ? 0 : minima.Max());
        for (int candidate = minimum; candidate <= 4; candidate++)
        {
            // 有本任务专用路线时不借另一档普通迷宫绕过；无专用路线的寻物和猎杀保持普通借路。
            if (connections.Length == 0)
            {
                if (!dungeon.AllowsDifficulty((byte)candidate, false)) continue;
            }
            else if (!connections.Any(maze => maze.ConnectionQuestId is { } route && maze.ConnectsQuest((uint)route, (byte)candidate) &&
                (dungeon.AllowsDifficulty((byte)candidate, true, route) ||
                 dungeon.AllowsOrdinaryQuestDifficulty((byte)candidate, route)))) continue;
            return (byte)candidate;
        }
        // 配置没有共同合法难度时不新设拒绝门禁，更不擅自降难度或改任务条件。
        return difficulty;

        void Add(StoryQuest quest)
        {
            bool epic = quest.Grade == "[epic]" || quest.MainQuest is > 0 && catalog is not null &&
                catalog.TryGet(quest.MainQuest.Value, out var parent) && parent.Grade == "[epic]";
            if (!epic || targets.Any(row => row.Id == quest.Id) ||
                !character.ActiveMainQuests.Any(row => row.QuestId == quest.Id && row.AcceptanceId != Guid.Empty) || !Pending(quest)) return;
            targets.Add(quest);
            if (catalog is not null && QuestRelationProgress.SequentialParent(quest, catalog))
            {
                int next = quest.IntData.FirstOrDefault(id => !character.CompletedQuests.Contains(id));
                if (next != 0 && catalog.TryGet(next, out var child) && !targets.Any(row => row.Id == child.Id) && Pending(child)) targets.Add(child);
            }
        }

        bool Pending(StoryQuest quest)
        {
            var active = character.ActiveMainQuests.FirstOrDefault(row => row.QuestId == quest.Id && row.AcceptanceId != Guid.Empty);
            if (active is null || active.ConditionState != 0) return true;
            // 寻物还需核对当前库存，不能把刚过期材料留下的旧scalar0当作已经达成。
            return QuestLifecyclePlan.ValidSeeking(quest) && !QuestItemProgress.HasSelector(quest) &&
                !QuestLifecyclePlan.HasMaterials(character, quest, now, catalog?.Items);
        }

        bool Related(StoryQuest quest, QuestDungeonSelector scope)
        {
            var limits = QuestTargetSchema.Read(quest).DungeonLimits;
            if (!limits.IsEmpty && !limits.Contains(dungeon.Id)) return false;
            return scope.Kind switch
            {
                QuestDungeonSelectorKind.Any => true,
                QuestDungeonSelectorKind.Dungeon => scope.Id == dungeon.Id,
                QuestDungeonSelectorKind.Recommended => DungeonLuckyStarPolicy.IsRecommendedLevel(dungeon, character.Level),
                _ => false
            };
        }
    }
}
