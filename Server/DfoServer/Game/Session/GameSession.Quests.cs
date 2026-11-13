using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 只闭合已达成、禁经验且无奖励的真实子任务；清图仍复验本次接取的持久通关凭据。
    // 父任务回调同时补归档范围类零奖励子任务，但必须已由 Matches 写入 scalar 0，并且人还在当前阶段地图的同一 NPC 半径内。
    public QuestCompletionReceipt? FinishReadyQuestChildren(QuestCatalog quests, QuestRewardParameterCatalog parameters,
        ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, CharacterGrowthCatalog growth, int? parentQuestId = null)
    {
        var children = owner.Execute(() =>
        {
            if (Selected is null) return Array.Empty<ushort>();
            var rows = Selected.ActiveMainQuests.Where(row => row.ConditionState == 0 &&
                quests.TryGet(row.QuestId,out var quest) && QuestRelationProgress.ZeroRewardChild(quest) &&
                (parentQuestId is not null || Phase == SessionPhase.Town || !QuestRelationProgress.ClearDialogueChild(quest,quests)) &&
                (parentQuestId is null || quest.MainQuest == parentQuestId &&
                    (QuestRelationProgress.TownLinkedChild(quest) || QuestRelationProgress.ClearDialogueChild(quest,quests) ||
                        QuestClientSceneProgress.SatisfiedTownRange(Selected,quest,towns))))
                .Select(row => row.QuestId).ToArray();
            if (parentQuestId is int parentId && quests.TryGet(parentId,out var parent))
                Array.Sort(rows,(left,right) => ChildOrder(parent.IntData,left).CompareTo(ChildOrder(parent.IntData,right)));
            return rows;
        });
        QuestCompletionReceipt? last = null;
        bool townRange = false;
        foreach (ushort id in children)
        {
            var result = FinishQuest(id,ushort.MaxValue,quests,parameters,items,goldLimits,pieces,growth);
            if (result.Receipt is { } receipt)
            {
                last = receipt;
                if (quests.TryGet(id,out var child) && QuestClientSceneProgress.TownRange(child)) townRange = true;
            }
            // 子任务提交后投影失效时，把这份回执交还给调用方关闭连接，不能继续交付后面的任务。
            if (last is { Commit.ProjectionCurrent: false }) break;
        }
        if (townRange && last is { Commit.ProjectionCurrent: true })
            last = last with { ArchivedTownRange = true };
        return last;
    }

    readonly record struct RangeArchiveGate(QuestFinishResult? Stop, ImmutableArray<QuestCompletionReceipt> Committed);

    // 父任务奖励或资格过不了预检时，范围子任务一笔都不归档。预检通过后才逐个提交；
    // 提交后投影失效必须原样返回，调用方关闭连接，不再交付父任务。
    private RangeArchiveGate CommitSatisfiedRangeChildren(StoryQuest parent, QuestCatalog quests, QuestRewardParameterCatalog parameters,
        ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, CharacterGrowthCatalog growth, ExpertJobScriptCatalog? expertJobs)
    {
        var captured = owner.Execute(() => (
            Character: !Ready || Selected is null || Phase != SessionPhase.Town ? null : Selected,
            SelectionEpoch, Phase, Generation: owner.Generation, Pending: pendingQuestCompletion));
        if (captured.Character is not { } expected) return new(null, []);
        var ids = new List<ushort>();
        foreach (int id in parent.IntData)
        {
            if (id is < 1 or > ushort.MaxValue || !quests.TryGet(id,out var child) || !QuestRelationProgress.ZeroRewardChild(child) ||
                !QuestClientSceneProgress.SatisfiedTownRange(expected,child,towns)) continue;
            ids.Add((ushort)id);
        }
        if (ids.Count == 0) return new(null, []);
        // 父任务自己的精确重试必须沿用冻结计划。此时不能再改子任务，否则原Expected对不上。
        // 补归档前的活动表在这份计划上，不从已经没有子任务的在线快照重算。
        if (captured.Pending is { } pending && pending.Plan.QuestId == parent.Id && pending.Plan.RequestedSelector == ushort.MaxValue &&
            pending.Plan.Multiplier == 1 && pending.Plan.Coupon is null && ReferenceEquals(pending.Plan.Expected, expected) &&
            pending.SelectionEpoch == captured.SelectionEpoch && pending.Phase == captured.Phase && pending.DungeonIdentity is null)
            return new(null, []);
        var rejection = new QuestRefusalReceipt(expected, captured.Generation, captured.SelectionEpoch, null) { Phase = captured.Phase };
        bool strongwayQuest = QuestStrongway2019Entry.Supports(parent);
        if (strongwayQuest && !IsCurrentStrongway2019QuestSchedule(CaptureStrongway2019QuestSchedule()))
            return new(new(QuestFinishOutcome.Rejected, RejectionReceipt: rejection)
                { Failure = QuestCompletionFailure.EligibilityChanged, Reason = "强者之路活动未开启" }, []);
        bool epicRoadQuest = QuestEpicRoad2019Entry.Supports(parent);
        if (epicRoadQuest && !IsCurrentEpicRoad2019QuestSchedule(CaptureEpicRoad2019QuestSchedule()))
            return new(new(QuestFinishOutcome.Rejected, RejectionReceipt: rejection)
                { Failure = QuestCompletionFailure.EligibilityChanged, Reason = "星河裂缝活动未开启" }, []);
        var now = DateTimeOffset.UtcNow;
        QuestStageState? stageState = stageGraph is null ? null : repository.LoadQuestStageState(owner, expected);
        QuestClearReceipt? clear = expected.CompletedQuests.Contains(parent.Id) ? null : ReadClearedQuest(checked((ushort)parent.Id))?.Clear;
        QuestClearReceipt? terminalClear = null;
        if (QuestRelationProgress.TerminalClearDialogue(parent,quests) is { } terminal && !expected.CompletedQuests.Contains(terminal.Id))
            terminalClear = ReadClearedQuest(checked((ushort)terminal.Id))?.Clear;
        var rank = QuestStateProgress.Type(parent) is 5 or 27 ? pvpRatings?.Capture(owner, expected.Id) : null;
        int? grade = rank is null ? null : pvpRatings!.Grade(rank);
        var preview = expected;
        foreach (ushort id in ids)
        {
            var childPrep = QuestCompletionPlan.Prepare(preview, quests.Get(id), ushort.MaxValue, quests, parameters, items, goldLimits, pieces,
                growth, now, null, 1, contracts?.Growth, stageGraph, stageState, experienceBonuses, PremiumPcRoom, expertJobs, null, null);
            if (childPrep.Outcome != QuestCompletionOutcome.Accepted || childPrep.Plan is null)
                return new(RejectPrepared(rejection, childPrep), []);
            preview = childPrep.Plan.ProjectedCharacter;
        }
        var parentPrep = QuestCompletionPlan.Prepare(preview, parent, ushort.MaxValue, quests, parameters, items, goldLimits, pieces, growth,
            now, clear, 1, contracts?.Growth, stageGraph, stageState, experienceBonuses, PremiumPcRoom, expertJobs, grade, terminalClear,
            strongway2019Active: strongwayQuest, epicRoad2019Active: epicRoadQuest);
        if (parentPrep.Outcome != QuestCompletionOutcome.Accepted || parentPrep.Plan is null)
            return new(RejectPrepared(rejection, parentPrep), []);
        var committed = ImmutableArray.CreateBuilder<QuestCompletionReceipt>();
        foreach (ushort id in ids)
        {
            var result = FinishQuest(id, ushort.MaxValue, quests, parameters, items, goldLimits, pieces, growth, expertJobs: expertJobs, archiveRangeChildren: false);
            if (result.Receipt is { Commit.ProjectionCurrent: false }) return new(result, []);
            if (result.Outcome != QuestFinishOutcome.Committed || result.Receipt is null)
                return new(result with { ArchivedChildren = committed.ToImmutable() }, []);
            committed.Add(result.Receipt);
        }
        return new(null, committed.ToImmutable());
    }

    static QuestFinishResult RejectPrepared(QuestRefusalReceipt rejection, QuestCompletionPreparation preparation) =>
        new(preparation.Outcome == QuestCompletionOutcome.InsufficientSpace && preparation.Failure == QuestCompletionFailure.InventoryCapacity
            ? QuestFinishOutcome.InsufficientSpace : QuestFinishOutcome.Rejected, RejectionReceipt: rejection)
        { Failure = preparation.Failure, Reason = preparation.Reason };

    static QuestFinishResult WithArchivedChildren(QuestFinishResult result, ImmutableArray<QuestCompletionReceipt> archived)
    {
        if (archived.Length == 0 || result.Outcome == QuestFinishOutcome.Committed) return result;
        if (result.RejectionReceipt is null)
        {
            var latest = archived[^1];
            result = result with { RejectionReceipt = new QuestRefusalReceipt(latest.Commit.Character, latest.Generation, latest.SelectionEpoch, latest.DungeonIdentity) { Phase = latest.Phase } };
        }
        return result with { ArchivedChildren = archived };
    }

    static int ChildOrder(IReadOnlyList<int> ids, int questId)
    {
        for (int index = 0; index < ids.Count; index++)
            if (ids[index] == questId) return index;
        return int.MaxValue;
    }

    private sealed record PendingQuestCompletion(QuestCompletionPlan Plan, long SelectionEpoch,
        SessionPhase Phase, DungeonRunIdentity? DungeonIdentity)
    {
        // 范围子任务已经提交、父任务结果还没装上会话时，574 的对照表。精确重试不能再向在线快照要这份表。
        public IReadOnlyList<ActiveMainQuest>? RangePreviousQuests { get; init; }
    }
    private PendingQuestCompletion? pendingQuestCompletion;

    public QuestFinishResult FinishSingleClearQuest(ushort questId, QuestCatalog quests, QuestRewardParameterCatalog parameters,
        ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, CharacterGrowthCatalog growth)
        => FinishQuest(questId, ushort.MaxValue, quests, parameters, items, goldLimits, pieces, growth);

    public QuestFinishResult FinishQuest(ushort questId, ushort selector, QuestCatalog quests, QuestRewardParameterCatalog parameters,
        ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, CharacterGrowthCatalog growth, ushort multiplier = 1,
        ExpertJobScriptCatalog? expertJobs = null, ushort? couponSlot = null, bool archiveRangeChildren = true)
    {
        ImmutableArray<QuestCompletionReceipt> archived = [];
        if (archiveRangeChildren && couponSlot is null && multiplier == 1 && selector == ushort.MaxValue &&
            quests.TryGet(questId,out var parentOpening) && QuestRelationProgress.SequentialParent(parentOpening,quests))
        {
            var gate = CommitSatisfiedRangeChildren(parentOpening,quests,parameters,items,goldLimits,pieces,growth,expertJobs);
            if (gate.Stop is { } stop) return SealUnpublishedRange(stop);
            archived = gate.Committed;
        }
        // 第一笔子任务提交前的活动表跟父计划一起留下。父提交抛异常或返回丢失时，局部 archived 不在了，精确重试仍用这份表改写 PreviousQuests。
        var rangePrevious = archived.Length > 0 ? archived[0].Commit.PreviousQuests : null;
        var prepared = FinishPreparedQuest(questId,selector,quests,parameters,items,goldLimits,pieces,growth,multiplier,expertJobs,couponSlot,rangePrevious);
        return SealUnpublishedRange(WithArchivedChildren(prepared, archived));
    }

    // 范围子任务已归档，但这次结果既不是可发布的父回执，也没有可发布的子回执。关闭连接，不把拒绝当成活动表已经对齐。
    QuestFinishResult SealUnpublishedRange(QuestFinishResult result)
    {
        if (result.Outcome == QuestFinishOutcome.Committed || result.ArchivedChildren.Length > 0 || result.CloseForUnpublishedRangeArchive)
            return result;
        bool unpublished = owner.Execute(() => pendingQuestCompletion?.RangePreviousQuests is { Count: > 0 });
        return unpublished ? result with { CloseForUnpublishedRangeArchive = true } : result;
    }

    QuestFinishResult FinishPreparedQuest(ushort questId, ushort selector, QuestCatalog quests, QuestRewardParameterCatalog parameters,
        ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, CharacterGrowthCatalog growth, ushort multiplier,
        ExpertJobScriptCatalog? expertJobs, ushort? couponSlot, IReadOnlyList<ActiveMainQuest>? rangePreviousQuests = null)
    {
        var captured = owner.Execute(() =>
        {
            if (AntonRejoinPresentationPending || pendingRental is not null || pendingCommunication is not null || pendingQuestLifecycle is not null || pendingQuestQuickClear is { CommitStarted: true } ||
                !Ready || Selected is null || Phase is not (SessionPhase.Town or SessionPhase.Dungeon or SessionPhase.Settlement) ||
                HasPendingFormalCard || formalDungeon?.Ground.HasPendingOperation == true || formalDungeon?.Run.HasPendingDeath == true || formalDungeon?.Run.HasPendingSettlement == true || HasPendingQuestScene)
                return (Character: (DfoServer.Game.Characters.Character?)null, SelectionEpoch, Phase,
                    Identity: (DungeonRunIdentity?)null, Clear: (QuestClearReceipt?)null, Pending: (PendingQuestCompletion?)null,
                    Entry: (DungeonEntryPlan?)null, Room: (DfoServer.GameWorld.Dungeons.DungeonRoomDefinition?)null,
                    CardsFinished: false, TerminalClear: (QuestClearReceipt?)null);
            var identity = Phase is SessionPhase.Dungeon or SessionPhase.Settlement ? formalDungeon?.Run.Snapshot.Identity : null;
            var room = identity is not null && formalDungeon is { } dungeon
                ? dungeon.Run.CurrentMonsterPlan.Room : null;
            var pending = pendingQuestCompletion;
            bool samePending = pending is not null && ReferenceEquals(pending.Plan.Expected, Selected) &&
                pending.SelectionEpoch == SelectionEpoch && pending.Phase == Phase && pending.DungeonIdentity == identity &&
                pending.Plan.QuestId == questId && pending.Plan.RequestedSelector == selector && pending.Plan.Multiplier == multiplier &&
                pending.Plan.Coupon?.Item.Position.Index == couponSlot;
            // 提交已发生但返回值丢失时，Selected仍是原旧快照；先以原计划查receipt，不能先用旧revision重读活动槽。
            bool completedParentChild = quests.TryGet(questId,out var target) &&
                QuestRelationProgress.CompletedParentChild(Selected,target,quests) is not null;
            // 不同请求不能用旧revision读取或替换未决领奖；同请求继续使用冻结清图凭据。
            var clear = couponSlot is null && (pending is null || samePending) && !Selected.CompletedQuests.Contains(questId) && !completedParentChild
                ? samePending ? pending!.Plan.AuthoritativeClear : ReadClearedQuest(questId)?.Clear : null;
            var terminalClear = samePending ? pending!.Plan.TerminalClear : couponSlot is null && pending is null && target is not null &&
                QuestRelationProgress.TerminalClearDialogue(target,quests) is { } terminal && !Selected.CompletedQuests.Contains(terminal.Id)
                    ? ReadClearedQuest(checked((ushort)terminal.Id))?.Clear : null;
            return (Character: Selected, SelectionEpoch, Phase, Identity: identity, Clear: clear, Pending: pendingQuestCompletion,
                Entry: formalDungeon?.Entry, Room: room,
                CardsFinished: formalDungeon?.Settlement is { Committed: true, SelectedCard: not null }, TerminalClear: terminalClear);
        });
        if (captured.Character is not { } expected)
            return new(QuestFinishOutcome.Unsupported) { Failure = QuestCompletionFailure.EligibilityChanged };
        var rejection = new QuestRefusalReceipt(expected, owner.Generation, captured.SelectionEpoch, captured.Identity) { Phase = captured.Phase };
        if (!quests.TryGet(questId, out var quest))
            return new(QuestFinishOutcome.Unsupported, RejectionReceipt: rejection) { Failure = QuestCompletionFailure.ResourceUnsupported };
        bool strongwayQuest = QuestStrongway2019Entry.Supports(quest);
        var strongwaySchedule = strongwayQuest ? CaptureStrongway2019QuestSchedule() : null;
        if (strongwayQuest && captured.Pending is null && !IsCurrentStrongway2019QuestSchedule(strongwaySchedule))
            return new(QuestFinishOutcome.Rejected, RejectionReceipt: rejection)
                { Failure = QuestCompletionFailure.EligibilityChanged, Reason = "强者之路活动未开启" };
        bool epicRoadQuest = QuestEpicRoad2019Entry.Supports(quest);
        var epicRoadSchedule = epicRoadQuest ? CaptureEpicRoad2019QuestSchedule() : null;
        if (epicRoadQuest && captured.Pending is null && !IsCurrentEpicRoad2019QuestSchedule(epicRoadSchedule))
            return new(QuestFinishOutcome.Rejected, RejectionReceipt: rejection)
                { Failure = QuestCompletionFailure.EligibilityChanged, Reason = "星河裂缝活动未开启" };
        if (QuestEligibility.Completed(expected).Contains(questId))
            return new(QuestFinishOutcome.AlreadyCompleted, RejectionReceipt: rejection) { Failure = QuestCompletionFailure.EligibilityChanged };
        bool dungeonClear = QuestDungeonClearProgress.Supports(quest);
        if (couponSlot is null && (quest.Condition == "[clear map]" || dungeonClear) && captured.Clear is null &&
            QuestRelationProgress.CompletedParentChild(expected,quest,quests) is null)
            return new(QuestFinishOutcome.Rejected, RejectionReceipt: rejection)
                { Failure = QuestCompletionFailure.EligibilityChanged, Reason = "缺少本次接取的通关凭据" };
        // 地点、NPC和实例目标在进度达成时验证；交付使用本次接取的持久事实，不因离开原场景而失效。
        // 带NPC的末清图子任务保留到结算后对话；原生34可转向父任务，父子在一个领奖事务中关闭。
        // 末清图对话的NPC是剧情交付人；已完成原实例结算翻牌后，不要求其城镇分身同时出现在MAP。
        // 其余顺序父保留实体NPC场景限制。
        // 各目标的实际材料、回答、子任务、角色状态及清图凭据仍由统一领奖规划复验。
        bool clearDialogue = couponSlot is null && (QuestRelationProgress.ClearDialogueChild(quest,quests) ||
            QuestRelationProgress.TerminalClearDialogue(quest,quests) is not null);
        if (clearDialogue && captured.Phase != SessionPhase.Town && !captured.CardsFinished)
            return new(QuestFinishOutcome.Rejected, RejectionReceipt: rejection)
                { Failure = QuestCompletionFailure.EligibilityChanged, Reason = "清图对话任务须先完成本实例结算和免费翻牌" };
        if (clearDialogue && captured.Phase != SessionPhase.Town &&
            (captured.Entry is not { } dialogueEntry || captured.Room is null ||
                dialogueEntry.RouteQuestId != (quest.MainQuest ?? quest.Id) ||
                !expected.ActiveMainQuests.Any(active => active.QuestId == quest.Id && QuestHuntProgress.Bound(active,dialogueEntry))))
            return new(QuestFinishOutcome.Rejected, RejectionReceipt: rejection)
                { Failure = QuestCompletionFailure.EligibilityChanged, Reason = "清图对话任务不属于当前原接取实例" };
        if (couponSlot is null && !clearDialogue && QuestRelationProgress.SequentialParent(quest,quests) && quest.CompleteNpc > 0 &&
            captured.Phase is SessionPhase.Dungeon or SessionPhase.Settlement)
        {
            int resolved = QuestClientSceneProgress.ResolveNpc(expected,quest.CompleteNpc,quest);
            bool townNpc = resolved > 0 && towns.Areas.Values.Any(area =>
                area.Default.Npcs.Any(npc => npc.Id == (uint)resolved) ||
                area.Phases.Any(phase => phase.Npcs.Any(npc => npc.Id == (uint)resolved)));
            if (townNpc)
            {
                bool atNpc = captured.Entry is { } entry && captured.Room is { NpcMetadataError: null } room &&
                    quest.Definition?.Dungeons.Any(row => row.DungeonId == entry.DungeonId) == true &&
                    expected.ActiveMainQuests.Any(active => active.QuestId == quest.Id && QuestHuntProgress.Bound(active,entry)) &&
                    room.Npcs.Any(npc => npc.Id == resolved);
                if (!atNpc)
                    return new(QuestFinishOutcome.Rejected, RejectionReceipt: rejection)
                        { Failure = QuestCompletionFailure.EligibilityChanged, Reason = "任务目标或NPC场景尚未满足" };
            }
        }
        // #897：安徒恩解锁任务与接取同一城镇合同，任意真实城镇地图交付，不限奈恩所在图，赛丽亚房间等城镇房间也可交付；副本/结算内不交付。
        if (QuestAntonUnlockEntry.Supports(quest) && (captured.Phase != SessionPhase.Town || !QuestAntonUnlockEntry.AtTown(expected, towns)))
            return new(QuestFinishOutcome.Rejected, RejectionReceipt: rejection)
                { Failure = QuestCompletionFailure.EligibilityChanged, Reason = "安徒恩解锁任务须在真实城镇地图交付" };
        if (captured.Pending is { } incomplete && (incomplete.Plan.QuestId != questId || incomplete.Plan.RequestedSelector != selector ||
            incomplete.Plan.Coupon?.Item.Position.Index != couponSlot ||
            incomplete.Plan.Multiplier != multiplier || incomplete.SelectionEpoch != captured.SelectionEpoch || incomplete.Phase != captured.Phase ||
            incomplete.DungeonIdentity != captured.Identity || !ReferenceEquals(incomplete.Plan.Expected, expected)))
            return new(QuestFinishOutcome.Rejected, RejectionReceipt: rejection) { Failure = QuestCompletionFailure.EligibilityChanged };
        var rank = QuestStateProgress.Type(quest) is 5 or 27 ? pvpRatings?.Capture(owner,expected.Id) : null;
        QuestCompletionPreparation preparation;
        if (captured.Pending is { } pending && pending.SelectionEpoch == captured.SelectionEpoch && pending.Phase == captured.Phase &&
            pending.DungeonIdentity == captured.Identity && ReferenceEquals(pending.Plan.Expected, expected) &&
            pending.Plan.AuthoritativeClear == captured.Clear && pending.Plan.RequestedSelector == selector && pending.Plan.Multiplier == multiplier &&
            pending.Plan.Coupon?.Item.Position.Index == couponSlot)
            preparation = new(QuestCompletionOutcome.Accepted, pending.Plan, "原领奖计划精确重试");
        else
        {
            QuestTicketSource? coupon=null;
            if (couponSlot is { } slot)
            {
                try
                {
                    if (slot>short.MaxValue || multiplier!=1 || selector!=ushort.MaxValue)
                        throw new InvalidDataException("单任务券需要原生单次34请求与Main源槽");
                    var position=new DfoServer.Game.Inventory.Models.InventorySlot(DfoServer.Game.Inventory.Models.InventoryListType.Main,(short)slot);
                    var source=expected.Inventory.Find(position) ?? throw new InvalidDataException("单任务券源槽为空");
                    coupon=QuestTicketSource.Create(expected,position,items.QuestTicket(source.Core.ItemId),DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    if (coupon.Definition.ActionIndex!=QuestTicketDefinition.CouponAction || !CanUseStackableItem(expected,source.Core.ItemId,places:coupon.Definition.Places))
                        throw new InvalidDataException("源不是110单任务券或当前使用条件不符");
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
                {
                    return new(QuestFinishOutcome.Rejected,RejectionReceipt:rejection)
                        {Failure=QuestCompletionFailure.EligibilityChanged,Reason=error.Message};
                }
            }
            preparation = QuestCompletionPlan.Prepare(expected, quest, selector, quests, parameters, items, goldLimits, pieces,
                growth, coupon is null ? DateTimeOffset.UtcNow : DateTimeOffset.FromUnixTimeSeconds(coupon.UtcSeconds), captured.Clear, multiplier,contracts?.Growth, coupon is null ? stageGraph : null,
                coupon is not null || stageGraph is null ? null : repository.LoadQuestStageState(owner, expected), experienceBonuses, PremiumPcRoom, expertJobs,
                rank is null ? null : pvpRatings!.Grade(rank), captured.TerminalClear, strongway2019Active: strongwayQuest,coupon:coupon, epicRoad2019Active: epicRoadQuest);
            if (preparation.Plan is { } planned) planned.PvpRating = rank;
            if (strongwayQuest && preparation.Plan is { } strongwayPlan)
                strongwayPlan.RequiredEvent = new(strongwaySchedule!, timeProvider ?? TimeProvider.System);
            if (epicRoadQuest && preparation.Plan is { } epicRoadPlan)
                epicRoadPlan.RequiredEvent = new(epicRoadSchedule!, timeProvider ?? TimeProvider.System);
        }
        return owner.Execute(() =>
        {
            if (AntonRejoinPresentationPending || pendingRental is not null || pendingCommunication is not null || pendingQuestLifecycle is not null || pendingQuestQuickClear is { CommitStarted: true } ||
                !ReferenceEquals(pendingQuestCompletion,captured.Pending) || !CanPublishQuestRefusal(rejection))
                throw new InvalidOperationException("领奖资源规划的原选角、角色快照或实例已失效");
            if (preparation.Outcome != QuestCompletionOutcome.Accepted || preparation.Plan is null)
                return new QuestFinishResult(preparation.Outcome == QuestCompletionOutcome.InsufficientSpace &&
                    preparation.Failure == QuestCompletionFailure.InventoryCapacity
                    ? QuestFinishOutcome.InsufficientSpace : QuestFinishOutcome.Rejected, RejectionReceipt: rejection) { Failure = preparation.Failure, Reason = preparation.Reason };
            var plan = preparation.Plan;
            if (plan.Coupon is { } coupon && !CanUseStackableItem(expected,coupon.Item.Core.ItemId,places:coupon.Definition.Places))
                return new QuestFinishResult(QuestFinishOutcome.Rejected,RejectionReceipt:rejection)
                    {Failure=QuestCompletionFailure.EligibilityChanged,Reason="单任务券提交前使用条件已变化"};
            if (strongwayQuest && captured.Pending is null && (!IsCurrentStrongway2019QuestSchedule(strongwaySchedule) ||
                plan.RequiredEvent is not { } required || !required.Matches(strongwaySchedule!)))
                return new QuestFinishResult(QuestFinishOutcome.Rejected, RejectionReceipt: rejection)
                    { Failure = QuestCompletionFailure.EligibilityChanged, Reason = "强者之路活动前提已变化" };
            if (epicRoadQuest && captured.Pending is null && (!IsCurrentEpicRoad2019QuestSchedule(epicRoadSchedule) ||
                plan.RequiredEvent is not { } epicRequired || !epicRequired.Matches(epicRoadSchedule!)))
                return new QuestFinishResult(QuestFinishOutcome.Rejected, RejectionReceipt: rejection)
                    { Failure = QuestCompletionFailure.EligibilityChanged, Reason = "星河裂缝活动前提已变化" };
            plan.BattlePassCompletion ??= DfoServer.Game.BattlePass.BattlePassQuestCompletion.Prepare(plan, timeProvider ?? TimeProvider.System);
            // 不明提交结果保留这份冻结计划；精确查询同接取receipt，不生成新的领奖身份。
            // 范围子任务的补归档前活动表必须在父提交之前写上，异常把局部回执冲掉之后精确重试还拿得到。
            var remembered = rangePreviousQuests ?? captured.Pending?.RangePreviousQuests;
            pendingQuestCompletion = new(plan, captured.SelectionEpoch, captured.Phase, captured.Identity)
                { RangePreviousQuests = remembered };
            DfoServer.Game.Quests.QuestCompletionCommit commit;
            try { commit = repository.CommitQuestCompletion(owner, plan); }
            catch (QuestTicketExpiredException error) when (plan.Coupon is not null)
            {
                pendingQuestCompletion=null;
                return new QuestFinishResult(QuestFinishOutcome.Rejected,RejectionReceipt:rejection)
                    {Failure=QuestCompletionFailure.EligibilityChanged,Reason=error.Message};
            }
            // 仓储回执的 PreviousQuests 是父计划当时的活动表，子任务已经不在里面。574 要从补归档前的表起比。
            if (remembered is { Count: > 0 })
                commit = commit with { PreviousQuests = remembered };
            var result = new QuestFinishResult(QuestFinishOutcome.Committed,
                new(commit, owner.Generation, captured.SelectionEpoch, captured.Identity)
                { Phase = captured.Phase, BattlePassMissionAt = (timeProvider ?? TimeProvider.System).GetUtcNow() });
            // 失效投影仍保留实际提交回执和原pending，交由Handler关闭连接，不安装外部变更后的角色。
            if (!commit.ProjectionCurrent) return result;
            Selected = commit.Character;
            completed = QuestEligibility.Completed(Selected);
            pendingQuestCompletion = null;
            return result;
        });
    }

    public bool CanPublishQuestCompletion(QuestCompletionReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        receipt.Commit.ProjectionCurrent && ReferenceEquals(Selected, receipt.Commit.Character) && Phase == receipt.Phase &&
        (receipt.DungeonIdentity is null ? Phase == SessionPhase.Town :
            formalDungeon?.Run.Snapshot.Identity == receipt.DungeonIdentity));

    public bool CanPublishQuestRefusal(QuestRefusalReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        ReferenceEquals(Selected, receipt.Character) && Phase == receipt.Phase &&
        (receipt.DungeonIdentity is null ? Phase == SessionPhase.Town :
            formalDungeon?.Run.Snapshot.Identity == receipt.DungeonIdentity));

    public QuestConditionReceipt? ReadClearedQuest(ushort questId) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase is not (SessionPhase.Town or SessionPhase.Dungeon or SessionPhase.Settlement))
            return null;
        if (Phase != SessionPhase.Town && formalDungeon?.Run.HasPendingDeath == true) return null;
        var clear = repository.LoadQuestClear(owner, Selected, questId);
        if (clear is null)
            return null;
        var identity = Phase == SessionPhase.Town ? null : formalDungeon?.Run.Snapshot.Identity;
        if (Phase != SessionPhase.Town && (identity is null || identity.InstanceId != clear.InstanceId ||
            identity.TaskAttempt != clear.TaskAttempt || !(formalDungeon!.Run.HasCommittedRoomClear(clear) ||
                Phase == SessionPhase.Settlement && identity.Epoch == clear.Epoch)))
            return null;
        return new QuestConditionReceipt(Selected, clear, owner.Generation, SelectionEpoch, identity);
    });

    public bool CanPublishQuestCondition(QuestConditionReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        ReferenceEquals(Selected, receipt.Character) &&
        (receipt.DungeonIdentity is null ? Phase == SessionPhase.Town :
            Phase is SessionPhase.Dungeon or SessionPhase.Settlement && formalDungeon?.Run.Snapshot.Identity == receipt.DungeonIdentity &&
                (formalDungeon.Run.HasCommittedRoomClear(receipt.Clear) ||
                    Phase == SessionPhase.Settlement && receipt.DungeonIdentity.Epoch == receipt.Clear.Epoch)));
}
