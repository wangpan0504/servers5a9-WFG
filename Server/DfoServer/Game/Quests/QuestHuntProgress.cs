using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal readonly record struct QuestHuntTarget(int DungeonId, int MinimumDifficulty, int MonsterId, int Count)
{
    public int EnemyType { get; init; } = 1;
}

// Full03分类2/6分别读取四/五元组，怪物、APC与被动物件消费服务端绑定的独立事件。
internal static class QuestHuntProgress
{
    // #256：猎杀目标 -2 只放行 6060。断头谷没有 [named monster]，稀有怪是地图品级里的精英（冠军/超级冠军，生成类型 1/2）。
    // 全 PVF 仅这一条使用 -2，其它任务仍拒绝。
    internal const int EliteMonsterSelector = -2, EliteQuestId = 6060;

    internal static bool EliteSelectorAllowed(StoryQuest quest, int dungeon, int monster) =>
        monster == EliteMonsterSelector && quest is { Id: EliteQuestId, Condition: "[hunt monster]", Grade: "[side]" } && dungeon == 7322;

    public static bool TryTargets(StoryQuest quest, out ImmutableArray<QuestHuntTarget> targets)
    {
        targets = [];
        if (QuestAutoDailyPlan.Supports(quest))
        {
            targets = [new(-1,-1,-1,quest.IntData[4]), new(-1,-1,-3,quest.IntData[9])];
            return true;
        }
        bool monsterOnly = quest.Condition == "[hunt monster]";
        int stride = monsterOnly ? 4 : 5;
        if (quest.Condition is not ("[hunt enemy]" or "[hunt monster]") || quest.SubType != -1 || !QuestCoverage.HasOrdinaryDeclarations(quest) ||
            quest.Grade == "[training]" || QuestEligibility.InspectResource(quest).Entry is not (QuestResourceEntry.Ordinary or QuestResourceEntry.Dialogue or QuestResourceEntry.Hidden or QuestResourceEntry.Creature) ||
            quest.Definition is not { Diagnostics.Length: 0 } ||
            quest.IntData.Count < stride || quest.IntData.Count > stride*3 || quest.IntData.Count % stride != 0) return false;
        var rows = ImmutableArray.CreateBuilder<QuestHuntTarget>();
        for (int at = 0; at < quest.IntData.Count; at += stride)
        {
            int dungeon = quest.IntData[at], difficulty = quest.IntData[at + 1], monster = quest.IntData[at + 2], count = quest.IntData[at + stride - 1];
            int enemyType = monsterOnly ? 1 : quest.IntData[at + 3];
            if (dungeon < -1 || difficulty is < -1 or > 3 || monster <= 0 && !(monsterOnly && (monster is -5 or -3 || EliteSelectorAllowed(quest,dungeon,monster))) ||
                enemyType is not (1 or 2 or 3) || count is < 1 or > 511)
                return false;
            rows.Add(new(dungeon,difficulty,monster,count) { EnemyType = enemyType });
        }
        targets = rows.ToImmutable(); return true;
    }

    public static uint Initial(StoryQuest quest)
    {
        if (!TryTargets(quest,out var targets)) throw new NotSupportedException("猎杀目标尚未接入权威事件或不符合三通道编码");
        uint scalar = 0;
        for (int at = 0; at < targets.Length; at++) scalar |= (uint)targets[at].Count << (at * 9);
        return scalar;
    }

    public static bool ValidScalar(StoryQuest quest, uint scalar) => TryTargets(quest,out var targets) &&
        scalar >> (targets.Length * 9) == 0 && targets.Select((target,at) => (scalar >> (at * 9) & 511) <= target.Count).All(value => value);

    public static bool Bound(ActiveMainQuest active, DungeonEntryPlan entry) => active.AcceptanceId != Guid.Empty &&
        entry.QuestAcceptances.Any(old => old.QuestId == active.QuestId && old.Slot == active.Slot && old.AcceptanceId == active.AcceptanceId);

    public static ImmutableArray<ActiveMainQuest> MonsterDeath(IReadOnlyList<ActiveMainQuest> active, DungeonEntryPlan entry,
        QuestCatalog quests, DungeonPlannedMonster monster) => monster.AiCharacter is null
        ? Death(active,entry,quests,monster.MonsterId,1,monster.SpawnType == 3,monster.SpawnType is 1 or 2,
            QuestAutoDailyPlan.CountableMonster(monster))
        : active.ToImmutableArray();

    public static ImmutableArray<ActiveMainQuest> AiCharacterDeath(IReadOnlyList<ActiveMainQuest> active, DungeonEntryPlan entry,
        QuestCatalog quests, int templateId) => Death(active,entry,quests,templateId,2);

    public static ImmutableArray<ActiveMainQuest> PassiveDeath(IReadOnlyList<ActiveMainQuest> active, DungeonEntryPlan entry,
        QuestCatalog quests, int templateId) => Death(active,entry,quests,templateId,3);

    // 演出动态创建的无奖励子任务物件，由真实终点清房确认；不依赖客户端猜测对象key。
    public static ImmutableArray<ActiveMainQuest> QuestObjectRoomClear(IReadOnlyList<ActiveMainQuest> active,
        DungeonEntryPlan entry, QuestCatalog quests, DungeonClearFact? clear)
    {
        if (clear is null || clear.Identity.CharacterId != entry.CharacterId || clear.Identity.QuestId != entry.QuestId ||
            clear.DungeonId != entry.DungeonId || clear.ClearMap is null || !entry.IsTerminalMap(clear.ClearMap) ||
            clear.ConfirmedDeathEvents.Count == 0) return active.ToImmutableArray();
        var targets = new List<(ActiveMainQuest Active,StoryQuest Quest,QuestHuntTarget Target)>();
        foreach (var row in active)
            if (row.ConditionState != 0 && Bound(row,entry) && quests.TryGet(row.QuestId,out var quest) &&
                TryTargets(quest,out var hunt) && ValidScalar(quest,row.ConditionState))
                for (int index = 0; index < hunt.Length; index++)
                {
                    var target = hunt[index];
                    if ((row.ConditionState >> (index*9) & 511) > 0 && target.EnemyType == 3 &&
                        (target.DungeonId == -1 || target.DungeonId == entry.DungeonId) &&
                        (target.MinimumDifficulty == -1 || entry.Difficulty >= target.MinimumDifficulty)) targets.Add((row,quest,target));
                }
        if (targets.Count != 1) return active.ToImmutableArray();
        var match = targets[0];
        if (match.Quest.Grade != "[sub]" || match.Quest.MainQuest is not > 0 || !QuestRelationProgress.ZeroRewardChild(match.Quest) ||
            !entry.Maze.HasActiveQuestConnection(match.Quest.MainQuest.Value) ||
            !active.Any(parent => parent.QuestId == match.Quest.MainQuest.Value && parent.ConditionState > 0 && Bound(parent,entry)))
            return active.ToImmutableArray();
        return PassiveDeath(active,entry,quests,match.Target.MonsterId);
    }

    private static ImmutableArray<ActiveMainQuest> Death(IReadOnlyList<ActiveMainQuest> active, DungeonEntryPlan entry,
        QuestCatalog quests, int templateId, int enemyType, bool ordinaryBoss = false, bool elite = false, bool dailyMonster = false) => active.Select(row =>
    {
        if (!Bound(row,entry) || !quests.TryGet(row.QuestId,out var quest) || !TryTargets(quest,out var targets) ||
            !ValidScalar(quest,row.ConditionState)) return row;
        bool daily = QuestAutoDailyPlan.Supports(quest);
        if (daily && (!dailyMonster || !QuestAutoDailyPlan.MatchesDungeon(quest,entry.DungeonId,entry.Difficulty))) return row;
        uint scalar = row.ConditionState;
        for (int at = 0; at < targets.Length; at++)
        {
            var target = targets[at]; uint count = scalar >> (at * 9) & 511;
            // Full03 type2的-3只接已确认普通SpawnType3；APC的rank8来源未闭合，不能并入。
            if (count != 0 && target.EnemyType == enemyType && (target.MonsterId > 0 && target.MonsterId == templateId || target.MonsterId == -5 ||
                target.MonsterId == -3 && ordinaryBoss || target.MonsterId == EliteMonsterSelector && elite ||
                daily && target.MonsterId == -1 && !ordinaryBoss && templateId is not (>= 50000 and <= 51000) and not (>= 56000 and <= 60000)) &&
                (target.DungeonId == -1 || target.DungeonId == entry.DungeonId) &&
                (target.MinimumDifficulty == -1 || entry.Difficulty >= target.MinimumDifficulty)) scalar -= 1u << (at * 9);
        }
        return scalar == row.ConditionState ? row : row with { ConditionState = scalar };
    }).ToImmutableArray();
}
