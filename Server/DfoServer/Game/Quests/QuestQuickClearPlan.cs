using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 独立无奖励规划；没有库存、成长或普通领奖计划，也不拥有在线角色状态。
internal sealed class QuestQuickClearPlan
{
    QuestQuickClearPlan(Character expected, QuestConnectionUnit unit, ActiveMainQuest active,
        StoryQuest question, StoryQuest marker, ImmutableArray<StoryQuest> interval, byte answer, QuestCatalog quests)
    {
        Expected = expected; Unit = unit; ActiveQuestion = active; Question = question; MarkerDefinition = marker;
        Interval = interval; Answer = answer;
        // V31完成消费者会把终点casecade关联marker加入当前角色完成集；与普通账号任务分开保存。
        var scope = interval.Select(quest => quest.Id).Prepend(question.Id).Append(marker.Id).ToHashSet();
        var completed = QuestEligibility.Completed(expected);
        NewCompletedQuestIds = scope.Where(id => !completed.Contains(id)).Order().ToImmutableArray();
        CompletedAfter = expected.CompletedQuests.Concat(NewCompletedQuestIds).Distinct().Order().ToImmutableArray();
        AnswersAfter = expected.QuestAnswers.SetItem(question.Id,answer);
        AccountCompletedAfter = expected.AccountCompletedQuests.ToImmutableArray();
        AccountAnswersAfter = expected.AccountQuestAnswers;
        RemovedActive = expected.ActiveMainQuests.Where(row => scope.Contains(row.QuestId)).ToImmutableArray();
        var remaining = QuestRelationProgress.Refresh(expected.ActiveMainQuests.Where(row => !scope.Contains(row.QuestId)).ToArray(),
            CompletedAfter.Concat(AccountCompletedAfter).ToArray(),quests);
        ActiveAfter = QuestStateProgress.Refresh(remaining,expected with
        {
            CompletedQuests = CompletedAfter.ToArray(), QuestAnswers = AnswersAfter
        },quests);
        Snapshot = JsonSerializer.Serialize(new
        {
            Unit.ParentKey, QuestionId = question.Id, AcceptanceId, expected.Revision, Answer,
            MarkerQuestId, Interval = interval.Select(quest => quest.Id).ToArray(),
            BeforeCharacter = new
            {
                expected.Id, expected.Slot, expected.Name, expected.Job, expected.GrowType, expected.Level,
                expected.EquipmentSlotFlags, expected.AcceptedStoryLevel, expected.Position, expected.Return,
                expected.DungeonState, expected.Skills, expected.Learning, expected.Inventory, expected.QuestCycleDay,
                DailyClaims = expected.QuestDailyClaims.OrderBy(row => row.Key).ToArray(),
                AccountDailyClaims = expected.AccountQuestDailyClaims.OrderBy(row => row.Key).ToArray()
            },
            BeforeActive = expected.ActiveMainQuests, ActiveAfter, BeforeCompleted = expected.CompletedQuests, CompletedAfter,
            BeforeAnswers = expected.QuestAnswers.OrderBy(row => row.Key).ToArray(), AnswersAfter = AnswersAfter.OrderBy(row => row.Key).ToArray(),
            AccountCompletedAfter, AccountAnswersAfter = AccountAnswersAfter.OrderBy(row => row.Key).ToArray()
        });
    }

    public Character Expected { get; }
    public QuestConnectionUnit Unit { get; }
    public ActiveMainQuest ActiveQuestion { get; }
    public Guid AcceptanceId => ActiveQuestion.AcceptanceId;
    public StoryQuest Question { get; }
    public byte Answer { get; }
    public StoryQuest MarkerDefinition { get; }
    public int MarkerQuestId => MarkerDefinition.Id;
    public ImmutableArray<StoryQuest> Interval { get; }
    public ImmutableArray<int> NewCompletedQuestIds { get; }
    public ImmutableArray<int> CompletedAfter { get; }
    public ImmutableDictionary<int,byte> AnswersAfter { get; }
    public ImmutableArray<int> AccountCompletedAfter { get; }
    public ImmutableDictionary<int,byte> AccountAnswersAfter { get; }
    public ImmutableArray<ActiveMainQuest> RemovedActive { get; }
    public ImmutableArray<ActiveMainQuest> ActiveAfter { get; }
    public string Snapshot { get; }

    public static bool TryCreate(Character expected, uint parentKey, QuestCatalog quests, QuestConnectionCatalog connections,
        out QuestQuickClearPlan? plan, out string reason)
    {
        plan = null;
        try
        {
            if (parentKey > int.MaxValue || !connections.TryGet((int)parentKey,out var unit) ||
                unit.QuestionQuestId is not { } questionId || unit.QuickClear is not { } quick)
                throw new InvalidDataException("parent key 没有完整可用的问题及 quick clear 声明");
            if (!quests.TryGet(questionId,out var question) || !QuestQuestionProgress.TryCount(question,out int count) ||
                quick.Answer < 0 || quick.Answer >= count)
                throw new InvalidDataException("question 定义或零基跳过答案不符");
            var activeRows = expected.ActiveMainQuests.Where(row => row.QuestId == questionId).Take(2).ToArray();
            if (activeRows is not [var active] || active.AcceptanceId == Guid.Empty || active.Slot >= 30 ||
                active.ConditionState != QuestQuestionProgress.Unanswered && active.ConditionState >= count ||
                QuestEligibility.Completed(expected).Contains(questionId))
                throw new InvalidDataException("真实 question 未接取、接取身份无效或已完成");
            var eligible = QuestEligibility.Evaluate(expected with
            {
                ActiveMainQuests = expected.ActiveMainQuests.Where(row => row != active).ToArray()
            },question,quests);
            if (!eligible.Eligible) throw new InvalidDataException("question 当前资格不符: " + eligible.Reason);
            if (expected.QuestAnswers.TryGetValue(questionId,out byte saved) && saved != quick.Answer ||
                expected.AccountQuestAnswers.TryGetValue(questionId,out byte accountSaved) && accountSaved != quick.Answer)
                throw new InvalidDataException("已有持久问题答案不能由跳过覆盖");
            if (unit.Sequence.IsDefaultOrEmpty || unit.Sequence.Select(row => row.QuestId).Distinct().Count() != unit.Sequence.Length ||
                unit.Sequence.Count(row => row.QuestId == questionId) != 1)
                throw new InvalidDataException("sequence 缺少唯一 question 或含重复任务引用");
            var sequence = unit.Sequence.Select(row => row.QuestId).ToArray();
            int start = Array.IndexOf(sequence,quick.StartQuestId);
            int end = Array.IndexOf(sequence,quick.EndQuestId);
            if (start < 0 || end < start) throw new InvalidDataException("quick clear 起止任务不在合法原序区间");
            // 按 sequence 的端点位置裁剪是资源推断；不是已恢复的原服遍历规则，也不按数字范围或依赖图扩展。
            var interval = unit.Sequence.Skip(start).Take(end-start+1).Select(row => quests.Get(row.QuestId)).ToImmutableArray();
            if (!quests.TryGet(quick.MarkerQuestId,out var marker) || marker.Definition is not { Diagnostics.Length: 0 } ||
                marker.Id == questionId || interval.Any(quest => quest.Id == marker.Id))
                throw new InvalidDataException("显式 marker 定义缺失或与实际完成范围重叠");
            if (!QuestRelationProgress.IsCascadeMarker(marker))
                throw new NotSupportedException("级联标记带有未确认的账号、职业或奖励行为");
            var markerUnknown = QuestCoverage.Unconfirmed(marker).Where(name => name != "synchronization account quest").ToArray();
            if (markerUnknown.Length != 0)
                throw new NotSupportedException("marker 声明尚未确认: " + marker.Id + ": " + string.Join(",",markerUnknown));
            Validate(question,quick.MarkerQuestId,quests);
            var completed = QuestEligibility.Completed(expected);
            foreach (var quest in interval)
            {
                Validate(quest,quick.MarkerQuestId,quests);
                if (quest.Condition == "[question]" && quest.Id != questionId && !completed.Contains(quest.Id))
                    throw new NotSupportedException("区间内问题尚无持久回答，不能猜造分支: " + quest.Id);
            }
            // marker按匹配完成消费者加入本角色；其跨角色同步不套用普通AccountQuest规则。
            plan = new(expected,unit,active,question,marker,interval,checked((byte)quick.Answer),quests);
            reason = "显式原序区间可无奖励跳过";
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            reason = error.Message;
            return false;
        }
    }

    static void Validate(StoryQuest quest, int markerId, QuestCatalog quests)
    {
        var definition = quest.Definition ?? throw new InvalidDataException("跳过任务没有完整定义: " + quest.Id);
        if (definition.Diagnostics.Length != 0) throw new InvalidDataException("跳过任务定义不可用: " + quest.Id + ": " + string.Join(",",definition.Diagnostics));
        if (definition.AccountQuest) throw new NotSupportedException("跳过范围包含账号任务: " + quest.Id);
        if (definition.JobChange != 0) throw new NotSupportedException("跳过范围包含职业变化: " + quest.Id);
        if (definition.AcceptanceRewards.Length != 0) throw new NotSupportedException("跳过范围包含接取赠品: " + quest.Id);
        if (quest.Condition is "[seeking]" or "[seek n meet npc]") throw new NotSupportedException("跳过范围包含寻物消耗: " + quest.Id);
        if (quest.RewardType is not (null or "[item]")) throw new NotSupportedException("跳过范围包含特殊奖励: " + quest.Id + ": " + quest.RewardType);
        if (definition.NpcVisibility.Any(row =>
            !(row.Condition is "[accept]" or "[clear]" && row.Visibility is "[show]" or "[hide]") &&
            !(row.Condition == "[clear]" && row.Visibility == "[delete]")))
            throw new NotSupportedException("跳过任务NPC可见性声明尚未确认: " + quest.Id);
        foreach (var field in definition.Fields.Where(field => field.Name is "quest title noti" or "solve video" or "casecade clear"))
        {
            bool valid = field.Name switch
            {
                "quest title noti" => QuestCoverage.IsTitleNotification(field),
                "solve video" => field.Children.Length == 0 && field.Values is [{ Type: 6, Text: not null }],
                "casecade clear" => field.Children.Length == 0 && field.Values is [{ Type: 0, Text: null } value] && value.Value == markerId,
                _ => false
            };
            if (!valid) throw new NotSupportedException("跳过声明不符合已普查形状: " + quest.Id + ": " + field.Name);
        }
        // 无奖励跳过保留完整显示声明，复用已确认的公共形状和引用合同。
        var unknown = QuestCoverage.Unconfirmed(quest);
        if (unknown.Length != 0) throw new NotSupportedException("跳过任务声明尚未确认: " + quest.Id + ": " + string.Join(",",unknown));
        if (!QuestCoverage.HasOrdinaryDeclarations(quest)) throw new NotSupportedException("跳过任务有未确认的分类、例外或属性: " + quest.Id);
        if (!QuestCoverage.HasRequiredReferences(quest,quests) || definition.RelationQuests.Any(id => !quests.TryGet(id,out _)))
            throw new InvalidDataException("跳过任务必需引用缺失: " + quest.Id);
        _ = QuestFixedRewardDefinition.Parse(quest,allowGold: true);
        _ = QuestFixedRewardDefinition.Parse(quest with { Rewards = definition.SelectRewards });
    }
}

internal sealed record QuestQuickClearCommit(Character Character, QuestQuickClearPlan Plan, bool Duplicate, bool ProjectionCurrent);
