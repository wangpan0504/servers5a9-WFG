using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Dungeons;

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
    private sealed record PendingCinematicClear(ushort QuestId, Guid AcceptanceId, long SelectionEpoch,
        DungeonRunIdentity Identity, DungeonMap Map, QuestCatalog Quests, ItemCatalog Items,
        GoldLimitCatalog GoldLimits, EpicPieceCatalog Pieces);
    private PendingCinematicClear? pendingCinematicClear;

    // 仅消费原加载动作和原接取绑定；复用33的完整场景校验及事务，异常时保留原事务恢复能力。
    public QuestLifecycleReceipt? CompletePendingCinematicClear()
    {
        var pending = owner.Execute(() =>
        {
            var value = pendingCinematicClear;
            if (value is null) return null;
            if (!Ready || Phase != SessionPhase.Dungeon || SelectionEpoch != value.SelectionEpoch ||
                formalDungeon?.Run is not { } run || run.LastLoadingFact is not { } loaded ||
                loaded.Identity != value.Identity || loaded.Map != value.Map || run.Snapshot.CurrentMap != value.Map ||
                Selected?.ActiveMainQuests.Any(row => row.QuestId == value.QuestId && row.AcceptanceId == value.AcceptanceId) != true)
            {
                pendingCinematicClear = null;
                return null;
            }
            return value;
        });
        if (pending is null) return null;
        var receipt = ChangeQuest(pending.QuestId, QuestLifecycleAction.Condition, pending.Quests, pending.Items,
            pending.GoldLimits, pending.Pieces, new QuestClientCondition(null, 0, 0));
        owner.Execute(() => { if (ReferenceEquals(pendingCinematicClear, pending)) pendingCinematicClear = null; return true; });
        return receipt;
    }

    // 仅接收已经批准的同坐标演出导航终末；初次加载和水平换房仍须等待37。
    private static bool CinematicLoadingTransition(FormalDungeon dungeon, StoryQuest movie)
    {
        var entry = dungeon.Entry;
        var run = dungeon.Run;
        var current = run.Snapshot.CurrentMap;
        if (entry.IsClassExperience || entry.IsPractice || entry.IsQuestReplay ||
            entry.RouteQuestId != movie.Id || !QuestClientSceneProgress.DungeonCinematic(movie) ||
            run.Phase != SinglePlayerDungeonPhase.Loading || run.LastLoadingFact is not { } loaded ||
            loaded.Map.Position != current.Position)
            return false;
        if (run.Snapshot.MapOperation == DungeonMapOperation.RestoreBase)
            // 原Run已批准从末层返回缓存基础图；3599的返回演出与基础图演出编号不同。
            return current == entry.BaseBossMap && current.Id == movie.CinematicMapId &&
                loaded.Map.Kind == "layered" && entry.Maze.LayeredMaps.Contains(loaded.Map);
        if (run.Snapshot.MapOperation != DungeonMapOperation.AdvanceLayer ||
            loaded.Map != entry.BaseBossMap || !entry.Maze.LayeredMaps.Contains(current))
            return false;
        // 3858在目标层开图时播放任务CMT，终末可先于该层37；不据此确认物件或清图。
        if (current.Id == movie.CinematicMapId) return true;
        if (movie.CinematicMapId != loaded.Map.Id ||
            entry.Maze.LayeredMaps.Count(map => map.Position == current.Position) != 1)
            return false;
        // 已形成基础清图事实的旧路线继续沿原结算换层合同处理。
        if (run.ClearFact?.ClearMap == loaded.Map) return true;
        // 普通Boss死亡演出可先换层再报告原图39；终点已由资源绑定到末层，33不依赖基础ClearFact。
        if (entry.BossMap == current && dungeon.Route.Rooms.Single(row => row.Room.Map == loaded.Map).Room.BossDeathLayerChange is { } death &&
            death.CinematicId == movie.IntData[0] && death.CinematicPath == movie.CinematicPath) return true;
        // 非战斗基础图的演出向下换层，按QST/CMT及已绑定末层判定。
        return entry.CinematicClearTerminalMap == current && movie.CinematicChangesLayer &&
            entry.QuestCinematicMapId == loaded.Map.Id && entry.QuestCinematicChangesLayer;
    }

    public QuestLifecycleReceipt? ChangeQuest(ushort questId, QuestLifecycleAction action, QuestCatalog quests,
        ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, QuestClientCondition? condition = null)
    {
        var capture = owner.Execute(() =>
        {
            // CMT在发送换层45后即可终末，33会早于新层37；只复用已加载基础图及本次批准换层身份。
            bool cinematicTransition = action == QuestLifecycleAction.Condition && condition is { Channel: null, Value: 0, Reserved: 0 } &&
                Phase == SessionPhase.Loading && formalDungeon is { } changing &&
                quests.TryGet(questId,out var movie) && CinematicLoadingTransition(changing,movie);
            Character? character = !AntonRejoinPresentationPending && Ready && Selected is not null && Phase != SessionPhase.Unselected &&
                (Phase != SessionPhase.Loading || cinematicTransition) &&
                pendingRental is null && pendingCommunication is null && pendingQuestCompletion is null && pendingQuestQuickClear is not { CommitStarted: true } &&
                !HasPendingFormalCard && formalDungeon?.Ground.HasPendingOperation != true &&
                (formalDungeon?.Run.HasPendingDeath != true ||
                    formalDungeon.Run.HasPendingPassiveClear && pendingQuestLifecycle?.Plan.SceneClear is { } pendingClear &&
                    formalDungeon.Run.IsPendingPassiveClear(pendingClear)) &&
                formalDungeon?.Run.HasPendingSettlement != true && !HasPendingQuestScene ? Selected : null;
            var instance = Phase is SessionPhase.Dungeon or SessionPhase.Settlement || cinematicTransition ? formalDungeon?.Run.Snapshot.Identity : null;
            var room = instance is not null && formalDungeon is { } dungeon
                ? dungeon.Run.CurrentMonsterPlan.Room : null;
            // 加载事实记录的是Advance前epoch；当前phase和完整Map确认已加载，提交再复验当前RunIdentity。
            bool loaded = instance is not null && formalDungeon is { Entry.IsClassExperience: false, Entry.IsPractice: false } current &&
                current.Run.Phase is SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement &&
                current.Run.LastLoadingFact is { } loading && loading.Map == current.Run.Snapshot.CurrentMap;
            return (Character: character, SelectionEpoch, Phase, Identity: instance, Entry: formalDungeon?.Entry,
                Pending: pendingQuestLifecycle,Skip: pendingQuestQuickClear, Room: room, Loaded: loaded,
                CinematicTransition: cinematicTransition, Communication: CommunicationSources(Selected));
        });
        if (capture.Character is not { } expected) return null;
        var refusal = new QuestLifecycleReceipt(expected, null, owner.Generation, capture.SelectionEpoch, capture.Phase, capture.Identity, "任务资格不符");
        // 未知提交结果只能恢复原请求；放弃或另一条件也不能覆盖原operation。
        if (capture.Pending is { } unfinished && (unfinished.Plan.QuestId != questId || unfinished.Plan.Action != action ||
            !ReferenceEquals(unfinished.Plan.Expected, expected) || unfinished.SelectionEpoch != capture.SelectionEpoch ||
            unfinished.Phase != capture.Phase || unfinished.Identity != capture.Identity || unfinished.Condition != condition))
            return refusal with { Reason = "前一任务事务尚待原请求恢复" };
        if (!quests.TryGet(questId, out var quest)) return refusal;
        if (quests.Items is { } facts && (QuestItemProgress.HasSelector(quest) || quest.TargetKind == GameWorld.Quests.QuestTargetKind.EquippedItem))
            QuestItemProgress.Prepare(expected.Inventory, facts);
        bool strongwayQuest = QuestStrongway2019Entry.Supports(quest) && action != QuestLifecycleAction.Giveup;
        var strongwaySchedule = strongwayQuest ? CaptureStrongway2019QuestSchedule() : null;
        if (strongwayQuest && capture.Pending is null && !IsCurrentStrongway2019QuestSchedule(strongwaySchedule))
            return refusal with { Reason = "强者之路活动未开启" };
        bool epicRoadQuest = QuestEpicRoad2019Entry.Supports(quest) && action != QuestLifecycleAction.Giveup;
        var epicRoadSchedule = epicRoadQuest ? CaptureEpicRoad2019QuestSchedule() : null;
        if (epicRoadQuest && capture.Pending is null && !IsCurrentEpicRoad2019QuestSchedule(epicRoadSchedule))
            return refusal with { Reason = "星河裂缝活动未开启" };
        if (action == QuestLifecycleAction.Accept && !CanAcceptHiddenQuest(expected,quest,quests,capture.SelectionEpoch,capture.Phase))
            return refusal with {Reason="隐藏对话任务缺少当前场景开启许可"};
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
        var rank = QuestStateProgress.Type(quest) is 5 or 27 && action != QuestLifecycleAction.Giveup
            ? pvpRatings?.Capture(owner,expected.Id) : null;
        int? grade = rank is null ? null : pvpRatings!.Grade(rank);
        QuestLifecyclePlan plan;
        try
        {
            if (action == QuestLifecycleAction.Accept && QuestAntonUnlockEntry.Supports(quest) &&
                (capture.Phase != SessionPhase.Town || !QuestAntonUnlockEntry.AtTown(expected, towns)))
                return refusal with { Reason = "安徒恩解锁任务须在真实城镇地图接取" };
            if (capture.Pending is { } pending && pending.Plan.QuestId == questId && pending.Plan.Action == action &&
                ReferenceEquals(pending.Plan.Expected, expected) && pending.SelectionEpoch == capture.SelectionEpoch &&
                pending.Phase == capture.Phase && pending.Identity == capture.Identity && pending.Condition == condition)
                plan = pending.Plan;
            else
            {
                plan = action switch
                {
                    QuestLifecycleAction.Accept => QuestLifecyclePlan.Accept(expected, quest, quests, items, goldLimits, pieces, DateTimeOffset.UtcNow, grade,
                        strongway2019Active: strongwayQuest, epicRoad2019Active: epicRoadQuest),
                    QuestLifecycleAction.Giveup => QuestLifecyclePlan.Giveup(expected, quest, quests, DateTimeOffset.UtcNow),
                    QuestLifecycleAction.Condition => ConditionPlan(),
                    _ => throw new NotSupportedException("客户端条件需经过类型及场景事实校验")
                };
                plan.PvpRating = rank;
                if (strongwayQuest)
                    plan.RequiredEvent = new(strongwaySchedule!, timeProvider ?? TimeProvider.System);
                if (epicRoadQuest)
                    plan.RequiredEvent = new(epicRoadSchedule!, timeProvider ?? TimeProvider.System);
                if (action == QuestLifecycleAction.Condition && expected.ActiveMainQuests.SequenceEqual(plan.ActiveAfter) &&
                    plan.SceneClear is null)
                    return refusal with { Outcome = QuestLifecycleOutcome.Unchanged, Reason = "当前条件已由实际场景或库存满足，无需再次保存" };
            }
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            return refusal with { Reason = error.Message };
        }
        return owner.Execute(() =>
        {
            if (AntonRejoinPresentationPending || pendingRental is not null || pendingCommunication is not null || pendingQuestCompletion is not null || pendingQuestQuickClear is { CommitStarted: true } ||
                !ReferenceEquals(pendingQuestLifecycle,capture.Pending) || !CanPublishQuestLifecycle(refusal)) throw new InvalidOperationException("任务规划的原选角或实际场景已失效");
            if (strongwayQuest && capture.Pending is null && (!IsCurrentStrongway2019QuestSchedule(strongwaySchedule) ||
                plan.RequiredEvent is not { } required || !required.Matches(strongwaySchedule!)))
                return refusal with { Reason = "强者之路活动前提已变化" };
            if (epicRoadQuest && capture.Pending is null && (!IsCurrentEpicRoad2019QuestSchedule(epicRoadSchedule) ||
                plan.RequiredEvent is not { } epicRequired || !epicRequired.Matches(epicRoadSchedule!)))
                return refusal with { Reason = "星河裂缝活动前提已变化" };
            pendingQuestLifecycle = new(plan, capture.SelectionEpoch, capture.Phase, capture.Identity, condition);
            var commit = repository.CommitQuestLifecycle(owner, plan);
            var receipt = refusal with { Character = commit.Character, Commit = commit, Outcome = QuestLifecycleOutcome.Committed, Reason = "任务事务已提交" };
            // 原操作已提交但投影失效时，只交回实际回执供Handler收尾；保留冻结计划，不安装新快照或绑定旧Run。
            if (!commit.ProjectionCurrent) return receipt with { Reason = "任务事务已提交，当前投影已失效，需重新选角恢复" };
            Selected = commit.Character;
            completed = QuestEligibility.Completed(Selected);
            if (capture.CinematicTransition && action == QuestLifecycleAction.Condition &&
                capture.Identity is { } loadingIdentity && capture.Entry is { } loadingEntry && capture.Room is { } loadingRoom &&
                PartyDungeon is null && loadingEntry.PassiveClearTerminalMap == loadingRoom.Map &&
                loadingEntry.Maze.ClearConditions is [{ Type: "destroy object", Count: 1 } clear] &&
                loadingRoom.PassiveObjects.Any(row => row.ObjectId == clear.TargetId) &&
                commit.Character.ActiveMainQuests.SingleOrDefault(row => row.QuestId == questId && QuestHuntProgress.Bound(row, loadingEntry)) is { } active)
                pendingCinematicClear = new(questId, active.AcceptanceId, capture.SelectionEpoch, loadingIdentity, loadingRoom.Map,
                    quests, items, goldLimits, pieces);
            if (action == QuestLifecycleAction.Giveup && pendingCinematicClear?.QuestId == questId)
                pendingCinematicClear = null;
            if (action == QuestLifecycleAction.Accept && capture.Phase is SessionPhase.Dungeon or SessionPhase.Settlement && formalDungeon is { } dungeon &&
                !dungeon.Entry.QuestAcceptances.Any(row => row.QuestId == quest.Id))
            {
                dungeon.Run.BindQuestAcceptance(commit.Character.ActiveMainQuests.Single(row => row.AcceptanceId == plan.AcceptanceId));
                formalDungeon = dungeon with { Entry = dungeon.Run.Entry };
            }
            if (plan.SceneClear is { } preparedClear && formalDungeon is { } sceneDungeon)
            {
                var clearResult = sceneDungeon.Run.ConfirmCommittedPassiveClear(preparedClear);
                if (clearResult.Outcome != DungeonRunOutcome.Accepted)
                    throw new InvalidOperationException("已提交场景清图的原实例准备无法确认");
                if (plan.SceneEvent is { } committedScene)
                    sceneDungeon.QuestSceneEvents[(sceneDungeon.Run.Snapshot.CurrentMap, committedScene.TemplateId, committedScene.SourceOrdinal)] =
                        new PendingQuestScene(plan) { Committed = true };
                SetState(SessionPhase.Settlement);
                receipt = receipt with { Phase = Phase, DungeonIdentity = sceneDungeon.Run.Snapshot.Identity };
            }
            pendingQuestLifecycle = null;
            return receipt;
        });

        QuestLifecyclePlan ConditionPlan()
        {
            if (capture.Entry?.IsPractice == true) throw new InvalidDataException("练习场景不推进任务条件");
            QuestCoverage.RequireOrdinaryDeclarations(quest);
            if (condition is { Channel: null, Value: 0 or 1, Reserved: 0 } &&
                quest.TargetKind == GameWorld.Quests.QuestTargetKind.CheckItem && QuestTargetRules.Supports(quest, quests))
                return QuestLifecyclePlan.SetCondition(expected, quest, QuestLifecyclePlan.MaterialState(expected.Inventory, quest,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), quests.Items), quests);
            if (condition is { Channel: null, Value: 0 or 1, Reserved: 0 } &&
                quest.TargetKind == GameWorld.Quests.QuestTargetKind.EquippedItem && quests.Items is { } equipmentFacts &&
                QuestTargetRules.Supports(quest, quests))
                return QuestLifecyclePlan.SetCondition(expected, quest, QuestItemProgress.Equipped(expected.Inventory, quest,
                    equipmentFacts, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) ? 0u : 1u, quests);
            if (QuestClientObservation.Supports(quest) && condition is { Channel: null, Value: 0, Reserved: 0 } &&
                capture.Phase == SessionPhase.Dungeon && formalDungeon?.Run is { } observedRun && capture.Identity == observedRun.Snapshot.Identity)
                return QuestLifecyclePlan.ObserveClientEvent(expected, quest, observedRun);
            if (QuestPvpProgress.Supports(quest) && condition is { Channel: null, Value: 0 or 1, Reserved: 0 } &&
                expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == quest.Id) is { } pvpObserved)
                return QuestLifecyclePlan.SetCondition(expected, quest, pvpObserved.ConditionState);
            if (QuestEventProgress.Supports(quest) && condition is { Channel: null, Value: 0 or 1, Reserved: 0 } &&
                expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == quest.Id) is { } observed)
                return QuestLifecyclePlan.SetCondition(expected, quest, observed.ConditionState);
            if (QuestDungeonClearProgress.NeedsClientCondition(quest) && condition is { Channel: null, Value: 0, Reserved: 0 } &&
                capture.Phase == SessionPhase.Settlement && formalDungeon?.Run is { } clearRun &&
                capture.Identity == clearRun.Snapshot.Identity)
            {
                var row = expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == quest.Id);
                if (row is { ConditionState: 0 }) return QuestLifecyclePlan.SetCondition(expected, quest, 0);
                return QuestLifecyclePlan.ObserveClientClear(expected, quest, clearRun);
            }
            if (QuestRentalProgress.Supports(quest) && condition is { Channel: null, Value: 0, Reserved: 0 } &&
                expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == quest.Id) is { AcceptanceId: var rentalAcceptance, ConditionState: 0 } &&
                rentalAcceptance != Guid.Empty)
                return QuestLifecyclePlan.SetCondition(expected, quest, 0);
            if (QuestExpertJobProgress.Tutorial(quest) && condition is { Channel: null, Value: 0, Reserved: 0 } &&
                expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == quest.Id) is { AcceptanceId: var expertAcceptance, ConditionState: 0 } &&
                expertAcceptance != Guid.Empty)
                return QuestLifecyclePlan.SetCondition(expected, quest, 0);
            if (QuestDungeonClearProgress.Supports(quest) && condition is { Channel: null, Value: 0, Reserved: 0 } &&
                expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == quest.Id) is { } clearActive &&
                QuestDungeonClearProgress.ValidScalar(quest,clearActive.ConditionState))
                return QuestLifecyclePlan.SetCondition(expected,quest,clearActive.ConditionState);
            if (QuestStateProgress.Type(quest) is { } stateType && condition is { Channel: null, Reserved: 0 } stateReport &&
                (stateReport.Value == 0 || stateType == 12 && stateReport.Value == 1))
                return QuestLifecyclePlan.SetCondition(expected,quest,QuestStateProgress.Scalar(expected,quest,grade),pvpGrade:grade);
            if (QuestRelationProgress.ThresholdParent(quest,quests) && condition is { Channel: null, Value: 0, Reserved: 0 })
                return QuestLifecyclePlan.SetCondition(expected,quest,
                    QuestRelationProgress.ThresholdScalar(quest,QuestEligibility.Completed(expected)));
            if (QuestRelationProgress.ListParent(quest, quests) && condition is { Channel: null, Value: 0 or 1, Reserved: 0 })
                return QuestLifecyclePlan.SetCondition(expected, quest, QuestRelationProgress.ListScalar(quest, QuestEligibility.Completed(expected)), quests);
            if (QuestRelationProgress.SequentialParent(quest,quests) && condition is { Channel: null, Reserved: 0 } parentReport &&
                parentReport.Value <= quest.IntData.Count &&
                (capture.Phase == SessionPhase.Town || capture.Entry is { } parentEntry && parentEntry.RouteQuestId == quest.Id &&
                    expected.ActiveMainQuests.Any(active => active.QuestId == quest.Id && QuestHuntProgress.Bound(active,parentEntry))))
                return QuestLifecyclePlan.SetCondition(expected,quest,QuestRelationProgress.Initial(quest,quests,expected.CompletedQuests,expected.ActiveMainQuests));
            // 问题请求由0202B480写入类型0和答案，尾字节不是其它条件通道的保留位。
            if (condition is { Channel: null, Value: 0 } answer && capture.Phase == SessionPhase.Town &&
                QuestQuestionProgress.OrdinaryChoice(quest,answer.Reserved,quests) &&
                HasDialogueNpc(quest.Npc))
                return QuestLifecyclePlan.SetCondition(expected,quest,answer.Reserved);
            if (quest.Condition is "[hunt enemy]" or "[hunt monster]" && QuestHuntProgress.TryTargets(quest,out var hunt) &&
                condition is { Channel: 0x10 or 0x20 or 0x40, Value: 0, Reserved: 0 } report &&
                (report.Channel == 0x10 ? 0 : report.Channel == 0x20 ? 1 : 2) < hunt.Length &&
                capture.Phase is SessionPhase.Dungeon or SessionPhase.Settlement && capture.Entry is { } entry &&
                expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == quest.Id) is { } accepted &&
                QuestHuntProgress.Bound(accepted,entry))
                // 客户端只读取当前权威结果，不按它上报的Boolean减少猎杀次数。
                return QuestLifecyclePlan.SetCondition(expected,quest,accepted.ConditionState);
            if (quest.Condition == "[seek n meet npc]" && quest.SubType is -1 or 0 && QuestLifecyclePlan.ValidSeeking(quest) &&
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
            // 0102A20F→0203D070→event42→02011B80：33是演出终末/关闭通知，不含CMT或接取身份；
            // 3858在70487按ESC跳过CMT2710时，0102BFE0只发33而不触发ON END CINEMATIC销毁69292，故已加载物件末层的33同时提交唯一物件清图。
            if (condition is { Channel: null, Value: 0, Reserved: 0 } &&
                QuestClientSceneProgress.DungeonCinematic(quest) && (capture.Loaded || capture.CinematicTransition) &&
                (capture.Phase is SessionPhase.Dungeon or SessionPhase.Settlement || capture.CinematicTransition) && capture.Entry is { } cinematicEntry &&
                capture.Room is { } cinematicRoom && QuestClientSceneProgress.OnDungeonCinematicMap(quest,cinematicEntry,cinematicRoom.Map) &&
                quest.Definition!.Dungeons.Any(row => row.DungeonId == cinematicEntry.DungeonId) &&
                expected.ActiveMainQuests.Any(active => active.QuestId == quest.Id && QuestHuntProgress.Bound(active,cinematicEntry)))
            {
                if (PartyDungeon is null && capture.Loaded && capture.Phase == SessionPhase.Dungeon &&
                    capture.Identity is { } sceneIdentity &&
                    cinematicEntry.PassiveClearTerminalMap == cinematicRoom.Map &&
                    cinematicEntry.Maze.ClearConditions is [{ Type: "destroy object", Count: 1 } clearCondition])
                {
                    int passiveOrdinal = -1;
                    for (int idx = 0; idx < cinematicRoom.PassiveObjects.Count; idx++)
                        if (cinematicRoom.PassiveObjects[idx].ObjectId == clearCondition.TargetId)
                        {
                            passiveOrdinal = idx;
                            break;
                        }
                    if (passiveOrdinal >= 0)
                    {
                        var cinematicPassive = owner.Execute(() =>
                        {
                            if (!Ready || Phase != capture.Phase || SelectionEpoch != capture.SelectionEpoch ||
                                !ReferenceEquals(Selected, expected) || formalDungeon is not { } current ||
                                current.Run.Snapshot.Identity != sceneIdentity ||
                                !current.Run.CanPreparePassiveClear(sceneIdentity, clearCondition.TargetId, passiveOrdinal))
                                return null;
                            string fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                                System.Text.Encoding.UTF8.GetBytes($"cinematic-passive:{quest.Id}:{sceneIdentity.InstanceId:N}:{cinematicRoom.Map.Id}:{clearCondition.TargetId}:{passiveOrdinal}")));
                            var source = new QuestSceneEvent(Guid.NewGuid(), sceneIdentity, checked((int)cinematicEntry.DungeonId),
                                cinematicRoom.Map.Id, clearCondition.TargetId, passiveOrdinal, fingerprint);
                            var prepared = current.Run.PreparePassiveClear(sceneIdentity, source.OperationId,
                                source.TemplateId, source.SourceOrdinal);
                            if (prepared is null) return null;
                            try
                            {
                                return QuestLifecyclePlan.SetCinematicPassiveClear(expected, quest, quests, current.Run.Entry,
                                    source, prepared, current.Run);
                            }
                            catch
                            {
                                current.Run.CancelUncommittedPassiveClear(prepared);
                                throw;
                            }
                        });
                        if (cinematicPassive is not null) return cinematicPassive;
                    }
                }
                return QuestLifecyclePlan.SetCondition(expected,quest,0);
            }
            // 6401 的条件文案是在希莫娜菜单开启贮藏箱。33 只在该任务 NPC 所在城镇地图上归零。
            // 0155C6F8 发事件 0x28（载荷 6401,0），02011CB0 按通道 0 的低 9 位次数调用 0200EE50；
            // kind 0 时 0200F0BB 写通道 0x10、value 归 0，02280690 发 7 字节 33。无通道形状不是这条路径。
            if (condition is { Channel: 0x10, Value: 0, Reserved: 0 } && QuestDazzlementStorage.Matches(quest) &&
                capture.Phase == SessionPhase.Town && HasDialogueNpc(quest.Npc))
                return QuestLifecyclePlan.SetCondition(expected,quest,0);
            // 普通寻物不足时原生0202C532发送value1；两种Boolean都只触发实际库存重算。
            // 会面仍只接受value0，不能借寻物的回退形状推进其它条件。
            if (condition is not { Channel: null, Value: 0 or 1, Reserved: 0 } ||
                condition is { Value: 1 } && quest.Condition != "[seeking]" ||
                capture.Phase is not (SessionPhase.Town or SessionPhase.Dungeon or SessionPhase.Settlement) ||
                quest.Definition is not { Diagnostics.Length: 0 } || !QuestLifecyclePlan.SeekingSubtype(quest) ||
                QuestEligibility.InspectResource(quest, strongway2019Active: strongwayQuest,
                    epicRoad2019Active: epicRoadQuest).Entry is not (QuestResourceEntry.Ordinary or QuestResourceEntry.Creature) && !QuestRelationProgress.DialogueChild(quest, quests) &&
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
            return QuestLifecyclePlan.SetCondition(expected, quest, scalar,quests);
        }

        bool HasDialogueNpc(int npcId)
        {
            if (capture.Phase == SessionPhase.Town)
                return QuestClientSceneProgress.DialogueNpc(expected,npcId,quest,quests,towns) ||
                    capture.Communication.Any(source => source.NpcId == QuestClientSceneProgress.ResolveNpc(expected,npcId,quest));
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
        if (questCatalog is null || !Ready || Selected is not { } expected || expected.QuestCycleDay > QuestCycle.Day(now) ||
            pendingRental is not null || pendingCommunication is not null || pendingQuestCompletion is not null || pendingQuestLifecycle is not null || pendingQuestQuickClear is { CommitStarted: true } || HasPendingQuestScene ||
            HasActiveDungeonContinuation || HasPendingQuestReplay ||
            HasPendingFormalCard || formalDungeon?.Ground.HasPendingOperation == true || formalDungeon?.Run.HasPendingDeath == true || formalDungeon?.Run.HasPendingSettlement == true)
            return null;
        int day = QuestCycle.Day(now);
        var daily = QuestAutoDailyPlan.Prepare(expected,questCatalog,day,allowAssignment: Phase == SessionPhase.Town);
        if (expected.QuestCycleDay == day && daily is null) return null;
        Selected = repository.AdvanceQuestCycle(owner, expected, day, daily);
        completed = QuestEligibility.Completed(Selected);
        return new QuestCycleReceipt(expected, Selected, owner.Generation, SelectionEpoch, Phase);
    });

    public bool CanPublishQuestCycle(QuestCycleReceipt receipt) => owner.Execute(() => Ready &&
        owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch && Phase == receipt.Phase &&
        ReferenceEquals(Selected, receipt.Character));
}
