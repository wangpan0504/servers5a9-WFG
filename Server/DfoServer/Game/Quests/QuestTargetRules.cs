using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal enum QuestTargetSupport { Unregistered, Unsupported, Implemented, AwaitingEvidence, InvalidParameters }

// 目标注册、形状支持及进度算法共用入口；具体玩法仍拥有自己的事实、事务和重放边界。
internal static class QuestTargetRules
{
    public static QuestTargetSupport Inspect(StoryQuest quest, QuestCatalog catalog)
    {
        if (quest.TargetRegistration is null) return QuestTargetSupport.Unregistered;
        var parameters = QuestTargetSchema.Read(quest);
        if (parameters.State == QuestParameterState.Unconfirmed) return QuestTargetSupport.AwaitingEvidence;
        if (parameters.State == QuestParameterState.Invalid) return QuestTargetSupport.InvalidParameters;
        return Supports(quest, catalog) ? QuestTargetSupport.Implemented : QuestTargetSupport.Unsupported;
    }

    public static bool Supports(StoryQuest quest, QuestCatalog catalog) => QuestTargetSchema.Read(quest).State == QuestParameterState.Parsed && quest.TargetKind switch
    {
        QuestTargetKind.CustomQuest => QuestRentalProgress.Supports(quest) || QuestExpertJobProgress.Craft(quest),
        QuestTargetKind.EpicProduction => QuestEpicProductionProgress.Supports(quest),
        QuestTargetKind.SelfDisjointItem => QuestExpertJobProgress.SelfDisjoint(quest),
        QuestTargetKind.MeetNpc => QuestStateProgress.Type(quest) is not null || quest.SubType is -1 or 0 && quest.IntData is [> 0],
        QuestTargetKind.Seeking or QuestTargetKind.SeekAndMeetNpc or QuestTargetKind.CheckItem => QuestLifecyclePlan.SeekingSubtype(quest) && QuestLifecyclePlan.ValidSeeking(quest) &&
            (!QuestItemProgress.HasSelector(quest) || catalog.Items is not null && quest.TargetKind == QuestTargetKind.CheckItem),
        QuestTargetKind.EquippedItem => catalog.Items is not null,
        QuestTargetKind.HuntEnemy or QuestTargetKind.HuntMonster => QuestHuntProgress.TryTargets(quest, out _),
        QuestTargetKind.QuestClear => QuestRelationProgress.SequentialParent(quest, catalog) || QuestRelationProgress.ListParent(quest, catalog),
        QuestTargetKind.ClearMultiQuest => QuestRelationProgress.ThresholdParent(quest, catalog),
        QuestTargetKind.ClearMap => QuestClearEligibility.IsSingleMap(quest),
        QuestTargetKind.ConditionUnderClear or QuestTargetKind.ClearDungeon => QuestDungeonClearProgress.Supports(quest),
        QuestTargetKind.ReachRange => QuestClientSceneProgress.TownRange(quest),
        QuestTargetKind.LookCinematic => QuestClientSceneProgress.Cinematic(quest, out _, out _) || QuestClientSceneProgress.DungeonCinematic(quest),
        QuestTargetKind.CustomMultiCondition => QuestDazzlementStorage.Matches(quest) || DfoServer.Game.Pvp.PvpTrainingProgress.Matches(quest),
        QuestTargetKind.Question => QuestQuestionProgress.TryCount(quest, out _),
        _ => QuestStateProgress.Type(quest) is not null || QuestPvpProgress.Supports(quest) ||
            QuestClientObservation.Supports(quest) && quest.TargetKind != QuestTargetKind.MeetPassiveObject ||
            QuestEventProgress.Supports(quest) && (quest.TargetKind != QuestTargetKind.GetItem || catalog.Items is not null)
    };

    public static uint Initial(Character character, StoryQuest quest, QuestCatalog catalog, DateTimeOffset now, int? pvpGrade) => quest.TargetKind switch
    {
        QuestTargetKind.CustomQuest when QuestRentalProgress.Supports(quest) || QuestExpertJobProgress.Craft(quest) => 1,
        QuestTargetKind.EpicProduction when QuestEpicProductionProgress.Supports(quest) => 1,
        QuestTargetKind.SelfDisjointItem when QuestExpertJobProgress.SelfDisjoint(quest) => 1,
        QuestTargetKind.MeetNpc when quest.SubType is -1 or 0 && quest.IntData is [> 0] => 1,
        // type4/sub1及type49的ACK31安装本次接取后即可归零；不伪造NPC点击或演出完成。
        QuestTargetKind.MeetNpc when QuestStateProgress.Type(quest) == 4 => 0,
        QuestTargetKind.ReachRange when QuestClientSceneProgress.TownRange(quest) => 1,
        QuestTargetKind.LookCinematic when QuestClientSceneProgress.Cinematic(quest, out _, out _) || QuestClientSceneProgress.DungeonCinematic(quest) => 1,
        QuestTargetKind.CustomMultiCondition when QuestDazzlementStorage.Matches(quest) => 1,
        QuestTargetKind.CustomMultiCondition when DfoServer.Game.Pvp.PvpTrainingProgress.Matches(quest) => DfoServer.Game.Pvp.PvpTrainingProgress.Initial(quest),
        QuestTargetKind.Question when QuestQuestionProgress.TryCount(quest, out _) => QuestQuestionProgress.Unanswered,
        QuestTargetKind.Seeking or QuestTargetKind.CheckItem when QuestLifecyclePlan.SeekingSubtype(quest) && QuestLifecyclePlan.ValidSeeking(quest) => QuestLifecyclePlan.MaterialState(character.Inventory, quest, now.ToUnixTimeSeconds(), catalog.Items),
        QuestTargetKind.SeekAndMeetNpc when quest.SubType is -1 or 0 && QuestLifecyclePlan.ValidSeeking(quest) => 512u | QuestLifecyclePlan.MaterialState(character.Inventory, quest, now.ToUnixTimeSeconds(), catalog.Items),
        QuestTargetKind.EquippedItem when catalog.Items is { } items => QuestItemProgress.Equipped(character.Inventory, quest, items, now.ToUnixTimeSeconds()) ? 0u : 1u,
        QuestTargetKind.ClearMap when QuestClearEligibility.IsSingleMap(quest) => 1,
        QuestTargetKind.ConditionUnderClear or QuestTargetKind.ClearDungeon when QuestDungeonClearProgress.Supports(quest) => QuestDungeonClearProgress.Initial(quest),
        QuestTargetKind.HuntEnemy or QuestTargetKind.HuntMonster => QuestHuntProgress.Initial(quest),
        QuestTargetKind.QuestClear when QuestRelationProgress.SequentialParent(quest, catalog) => QuestRelationProgress.Initial(quest, catalog, character.CompletedQuests),
        QuestTargetKind.QuestClear when QuestRelationProgress.ListParent(quest, catalog) => QuestRelationProgress.ListScalar(quest, QuestEligibility.Completed(character)),
        QuestTargetKind.ClearMultiQuest when QuestRelationProgress.ThresholdParent(quest, catalog) => QuestRelationProgress.Initial(quest, catalog, QuestEligibility.Completed(character)),
        QuestTargetKind.PlayOnly when QuestStateProgress.Type(quest) == 49 => 0,
        _ when QuestStateProgress.Type(quest) == 27 => QuestStateProgress.Scalar(character, quest, pvpGrade),
        _ when QuestPvpProgress.Supports(quest) => QuestPvpProgress.Initial(quest),
        _ when QuestClientObservation.Supports(quest) => QuestTargetSchema.Read(quest).Initial,
        _ when QuestEventProgress.Supports(quest) => QuestEventProgress.Initial(quest),
        _ when QuestStateProgress.Type(quest) is not null => QuestStateProgress.Scalar(character, quest, pvpGrade),
        _ => throw new NotSupportedException($"任务目标尚未接入: {quest.Condition}/{quest.SubType}")
    };

    public static byte CompletionType(StoryQuest quest, QuestCatalog catalog) => Supports(quest, catalog) &&
        quest.TargetRegistration?.CompletionType is { } type ? type :
        throw new NotSupportedException($"任务交付类型尚未核验: {quest.Condition}/{quest.SubType}");

    public static IEnumerable<QuestDropDefinition> Drops(StoryQuest quest, DungeonEntryPlan entry)
    {
        foreach (var row in quest.Definition!.Drops) yield return row;
        if (quest.TargetKind != QuestTargetKind.Seeking || quest.SubType != 20 || !entry.RecommendedQuestItems ||
            !QuestSeekingObjective.TryRead(quest, out var objective) || objective.MinimumClearDifficulty is not { } difficulty ||
            difficulty != -1 && entry.Difficulty < difficulty) yield break;
        // 当前PVF声明通关取物但没有概率行：兼容规则为每次合格通关一件，不在地面创建对象。
        for (int at = 0; at < objective.MaterialLength; at += 2)
        {
            int item = quest.IntData[at];
            if (quest.Definition.Drops.Any(row => row.ItemId == item)) continue;
            // 同一种材料的需求求和，避免重复目标行产生两次发放。
            if (Enumerable.Range(0, at / 2).Any(index => quest.IntData[index * 2] == item)) continue;
            // 需求上限由共享寻物发放逻辑以long汇总，保留重复材料行的合计语义。
            yield return new(0, 0, checked((int)entry.DungeonId), difficulty, item, 1, 100, -1);
        }
    }
}
