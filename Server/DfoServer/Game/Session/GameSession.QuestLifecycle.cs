using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed record QuestLifecycleReceipt(Character Character, QuestLifecycleCommit? Commit, long Generation, long SelectionEpoch,
    SessionPhase Phase, DungeonRunIdentity? DungeonIdentity, string Reason)
{
    public QuestLifecycleOutcome Outcome { get; init; }
}
internal sealed record QuestCycleReceipt(Character Before, Character Character, long Generation, long SelectionEpoch, SessionPhase Phase);

internal sealed partial class GameSession
{
    private sealed record PendingQuestLifecycle(QuestLifecyclePlan Plan, long SelectionEpoch, SessionPhase Phase,
        DungeonRunIdentity? Identity, QuestClientCondition? Condition);
    private PendingQuestLifecycle? pendingQuestLifecycle;

    public QuestLifecycleReceipt? ChangeQuest(ushort questId, QuestLifecycleAction action, QuestCatalog quests,
        ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, QuestClientCondition? condition = null)
    {
        var capture = owner.Execute(() =>
        {
            Character? character = Ready && Selected is not null && Phase is not (SessionPhase.Unselected or SessionPhase.Loading) &&
                !HasPendingFormalCard && formalDungeon?.Ground.HasPendingOperation != true && formalDungeon?.Run.HasPendingDeath != true &&
                formalDungeon?.Run.HasPendingSettlement != true && !HasPendingQuestScene ? Selected : null;
            var instance = Phase is SessionPhase.Dungeon or SessionPhase.Settlement ? formalDungeon?.Run.Snapshot.Identity : null;
            var room = instance is not null && formalDungeon is { } dungeon
                ? dungeon.Route.Rooms.Single(row => row.Room.Map == dungeon.Run.Snapshot.CurrentMap).Room : null;
            return (Character: character, SelectionEpoch, Phase, Identity: instance, Entry: formalDungeon?.Entry,
                Pending: pendingQuestLifecycle,Skip: pendingQuestQuickClear, Room: room);
        });
        if (capture.Character is not { } expected) return null;
        var refusal = new QuestLifecycleReceipt(expected, null, owner.Generation, capture.SelectionEpoch, capture.Phase, capture.Identity, "任务资格不符");
        if (!quests.TryGet(questId, out var quest)) return refusal;
        // 觉醒券本地消费者补发关联活动任务32；只确认本次已提交券事务实际关闭的接取槽。
        if (action == QuestLifecycleAction.Giveup && expected.ActiveMainQuests.All(row => row.QuestId != questId) &&
            capture.Skip is { Plan.IsTicket: true,Plan.ProfessionGrowType: not null,Committed: not null } ticket &&
            SameManualPending(ticket,expected,capture.SelectionEpoch,capture.Phase,capture.Identity) &&
            ticket.Plan.RemovedActive.Any(row => row.QuestId == questId))
            return refusal with { Outcome = QuestLifecycleOutcome.Unchanged, Reason = "觉醒券已原子关闭此活动任务" };
        // Full03父任务ACK34会补发未完成子任务32；父交付事务已关闭的子槽只确认，不再回收或写库。
        if (action == QuestLifecycleAction.Giveup && expected.ActiveMainQuests.All(row => row.QuestId != questId) &&
            !QuestEligibility.Completed(expected).Contains(questId) &&
            quest.MainQuest is { } parentId && QuestEligibility.Completed(expected).Contains(parentId) &&
            quests.TryGet(parentId,out var closedParent) && QuestRelationProgress.ThresholdParent(closedParent,quests) &&
            closedParent.IntData.Contains(questId))
            return refusal with { Outcome = QuestLifecycleOutcome.Unchanged, Reason = "阈值父任务已交付并关闭未完成子任务" };
        QuestLifecyclePlan plan;
        try
        {
            if (capture.Pending is { } pending && pending.Plan.QuestId == questId && pending.Plan.Action == action &&
                ReferenceEquals(pending.Plan.Expected, expected) && pending.SelectionEpoch == capture.SelectionEpoch &&
                pending.Phase == capture.Phase && pending.Identity == capture.Identity && pending.Condition == condition)
                plan = pending.Plan;
            else
            {
                plan = action switch
                {
                    QuestLifecycleAction.Accept => QuestLifecyclePlan.Accept(expected, quest, quests, items, goldLimits, pieces, DateTimeOffset.UtcNow),
                    QuestLifecycleAction.Giveup => QuestLifecyclePlan.Giveup(expected, quest, quests, DateTimeOffset.UtcNow),
                    QuestLifecycleAction.Condition => ConditionPlan(),
                    _ => throw new NotSupportedException("客户端条件需经过类型及场景事实校验")
                };
                if (action == QuestLifecycleAction.Condition && expected.ActiveMainQuests.SequenceEqual(plan.ActiveAfter))
                    return refusal with { Outcome = QuestLifecycleOutcome.Unchanged, Reason = "当前条件已由实际场景或库存满足，无需再次保存" };
            }
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            return refusal with { Reason = error.Message };
        }
        return owner.Execute(() =>
        {
            if (!CanPublishQuestLifecycle(refusal)) throw new InvalidOperationException("任务规划的原选角或实际场景已失效");
            pendingQuestLifecycle = new(plan, capture.SelectionEpoch, capture.Phase, capture.Identity, condition);
            var commit = repository.CommitQuestLifecycle(owner, plan);
            Selected = commit.Character;
            completed = QuestEligibility.Completed(Selected);
            if (action == QuestLifecycleAction.Accept && capture.Phase is SessionPhase.Dungeon or SessionPhase.Settlement && formalDungeon is { } dungeon &&
                !dungeon.Entry.QuestAcceptances.Any(row => row.QuestId == quest.Id))
            {
                dungeon.Run.BindQuestAcceptance(commit.Character.ActiveMainQuests.Single(row => row.AcceptanceId == plan.AcceptanceId));
                formalDungeon = dungeon with { Entry = dungeon.Run.Entry };
            }
            pendingQuestLifecycle = null;
            return refusal with { Character = commit.Character, Commit = commit, Outcome = QuestLifecycleOutcome.Committed, Reason = "任务事务已提交" };
        });

        QuestLifecyclePlan ConditionPlan()
        {
            QuestCoverage.RequireOrdinaryDeclarations(quest);
            if (QuestDungeonClearProgress.TryTarget(quest,out _) && condition is { Channel: null, Value: 0, Reserved: 0 } &&
                expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == quest.Id) is { } clearActive &&
                QuestDungeonClearProgress.ValidScalar(quest,clearActive.ConditionState))
                return QuestLifecyclePlan.SetCondition(expected,quest,clearActive.ConditionState);
            if (QuestStateProgress.Type(quest) is { } stateType && condition is { Channel: null, Reserved: 0 } stateReport &&
                (stateReport.Value == 0 || stateType == 12 && stateReport.Value == 1))
                return QuestLifecyclePlan.SetCondition(expected,quest,QuestStateProgress.Scalar(expected,quest));
            if (QuestRelationProgress.ThresholdParent(quest,quests) && condition is { Channel: null, Value: 0, Reserved: 0 })
                return QuestLifecyclePlan.SetCondition(expected,quest,
                    QuestRelationProgress.ThresholdScalar(quest,QuestEligibility.Completed(expected)));
            if (QuestRelationProgress.SequentialParent(quest,quests) && condition is { Channel: null, Reserved: 0 } parentReport &&
                parentReport.Value <= quest.IntData.Count &&
                (capture.Phase == SessionPhase.Town || capture.Entry is { } parentEntry && parentEntry.RouteQuestId == quest.Id &&
                    expected.ActiveMainQuests.Any(active => active.QuestId == quest.Id && QuestHuntProgress.Bound(active,parentEntry))))
                return QuestLifecyclePlan.SetCondition(expected,quest,QuestRelationProgress.Initial(quest,quests,expected.CompletedQuests));
            if (condition is { Channel: null, Reserved: 0 } answer && capture.Phase == SessionPhase.Town &&
                QuestQuestionProgress.OrdinaryChoice(quest,answer.Value,quests) &&
                QuestClientSceneProgress.DialogueNpc(expected,quest.IntData[0],quest,quests,towns))
                return QuestLifecyclePlan.SetCondition(expected,quest,answer.Value);
            if (quest.Condition is "[hunt enemy]" or "[hunt monster]" && QuestHuntProgress.TryTargets(quest,out var hunt) &&
                condition is { Channel: 0x10 or 0x20 or 0x40, Value: 0, Reserved: 0 } report &&
                (report.Channel == 0x10 ? 0 : report.Channel == 0x20 ? 1 : 2) < hunt.Length &&
                capture.Phase is SessionPhase.Dungeon or SessionPhase.Settlement && capture.Entry is { } entry &&
                expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == quest.Id) is { } accepted &&
                QuestHuntProgress.Bound(accepted,entry))
                // 客户端只读取当前权威结果，不按它上报的Boolean减少猎杀次数。
                return QuestLifecyclePlan.SetCondition(expected,quest,accepted.ConditionState);
            if (quest.Condition == "[seek n meet npc]" && quest.SubType == -1 && QuestLifecyclePlan.ValidSeeking(quest) &&
                (capture.Phase == SessionPhase.Town || HasDialogueNpc(quest.IntData[^1])) && quest.Definition is { Diagnostics.Length: 0 } &&
                condition is { Channel: 0x10 or 0x20, Reserved: 0 } seekReport &&
                expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == quest.Id) is { } seekActive && seekActive.ConditionState <= 1023)
            {
                uint material = QuestLifecyclePlan.MaterialState(expected.Inventory,quest,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                uint seekScalar = (seekActive.ConditionState & 512u) | material;
                if (seekReport.Channel == 0x20)
                {
                    if (seekReport.Value != 0 || material != 0 ||
                        !HasDialogueNpc(quest.IntData[^1]))
                        throw new InvalidDataException("寻物会面需要实际足额库存和当前目标NPC");
                    seekScalar = 0;
                }
                // 10只是完整库存谓词的重算信号；客户端Boolean不能当成材料减量。
                return QuestLifecyclePlan.SetCondition(expected,quest,seekScalar);
            }
            if (condition is { Channel: null, Value: 0, Reserved: 0 } && capture.Phase == SessionPhase.Town &&
                QuestClientSceneProgress.Matches(expected,quest,towns))
                return QuestLifecyclePlan.SetCondition(expected,quest,0);
            if (condition is not { Channel: null, Value: 0, Reserved: 0 } ||
                capture.Phase is not (SessionPhase.Town or SessionPhase.Dungeon or SessionPhase.Settlement) ||
                quest.Definition is not { Diagnostics.Length: 0 } || quest.SubType != -1 ||
                QuestEligibility.InspectResource(quest).Entry != QuestResourceEntry.Ordinary && !QuestRelationProgress.DialogueChild(quest, quests) &&
                    !QuestHiddenEntry.Direct(quest,quests))
                throw new InvalidDataException("客户端条件需要已确认类型和当前真实场景");
            uint scalar;
            if (capture.Phase == SessionPhase.Town && quest.Condition == "[seeking]" && QuestLifecyclePlan.ValidSeeking(quest))
                scalar = QuestLifecyclePlan.HasMaterials(expected, quest, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) ? 0u : 1u;
            else if (quest.Condition == "[meet npc]" && quest.IntData.Count == 1 && quest.IntData[0] > 0 &&
                HasDialogueNpc(quest.IntData[0]))
                scalar = 0;
            else
                throw new NotSupportedException("任务目标或NPC场景尚未满足");
            return QuestLifecyclePlan.SetCondition(expected, quest, scalar);
        }

        bool HasDialogueNpc(int npcId)
        {
            if (capture.Phase == SessionPhase.Town)
                return QuestClientSceneProgress.DialogueNpc(expected,npcId,quest,quests,towns);
            if (capture.Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement) ||
                capture.Entry is not { } entry || capture.Room is not { NpcMetadataError: null } room ||
                quest.Definition?.Dungeons.Any(row => row.DungeonId == entry.DungeonId) != true ||
                !expected.ActiveMainQuests.Any(active => active.QuestId == quest.Id && QuestHuntProgress.Bound(active,entry))) return false;
            int resolved = QuestClientSceneProgress.ResolveNpc(expected,npcId,quest);
            return resolved > 0 && room.Npcs.Any(npc => npc.Id == resolved);
        }
    }

    public bool CanPublishQuestLifecycle(QuestLifecycleReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch && Phase == receipt.Phase &&
        ReferenceEquals(Selected, receipt.Character) && (receipt.Commit?.ProjectionCurrent ?? true) &&
        (receipt.DungeonIdentity is null || formalDungeon?.Run.Snapshot.Identity == receipt.DungeonIdentity));

    public QuestCycleReceipt? AdvanceQuestCycle(DateTimeOffset now) => owner.Execute(() =>
    {
        if (questCatalog is null || !Ready || Selected is not { } expected || expected.QuestCycleDay >= QuestCycle.Day(now) ||
            pendingQuestCompletion is not null || pendingQuestLifecycle is not null || pendingQuestQuickClear is { CommitStarted: true } || HasPendingQuestScene ||
            pendingDungeonContinuation is not null || HasPendingQuestReplay ||
            HasPendingFormalCard || formalDungeon?.Ground.HasPendingOperation == true || formalDungeon?.Run.HasPendingDeath == true || formalDungeon?.Run.HasPendingSettlement == true)
            return null;
        Selected = repository.AdvanceQuestCycle(owner, expected, QuestCycle.Day(now));
        completed = QuestEligibility.Completed(Selected);
        return new QuestCycleReceipt(expected, Selected, owner.Generation, SelectionEpoch, Phase);
    });

    public bool CanPublishQuestCycle(QuestCycleReceipt receipt) => owner.Execute(() => Ready &&
        owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch && Phase == receipt.Phase &&
        ReferenceEquals(Selected, receipt.Character));
}
