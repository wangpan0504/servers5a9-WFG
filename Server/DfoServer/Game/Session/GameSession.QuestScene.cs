using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    private bool HasPendingQuestScene => formalDungeon?.QuestSceneEvents.Values.Any(value => !value.Committed) == true;
    private sealed class PendingQuestScene(QuestLifecyclePlan plan)
    {
        public QuestLifecyclePlan Plan { get; } = plan;
        public bool Committed { get; set; }
    }

    public QuestLifecycleReceipt? ObserveFormalPassiveDeath(uint templateId, bool checksum,
        string fingerprint, DungeonRoomCatalog resources)
    {
        // Full03 +1754与构造参数/资源查询同源；此分支没有普通怪物实例key。
        if (templateId is 0 or > int.MaxValue || !checksum || fingerprint.Length != 64 || questCatalog is null)
            return null;
        return ObserveFormalPassiveEvent(templateId,fingerprint,resources);
    }

    // 诊断用：被动物件死亡/任务触发被拒时说明原因，不改变任何状态。
    public string DiagnosePassiveEvent(uint templateId, DungeonRoomCatalog resources) => owner.Execute(() =>
    {
        var dungeon = formalDungeon;
        if (dungeon is null || !Ready) return "无正式副本或会话未就绪";
        if (dungeon.Entry.IsClassExperience || dungeon.Entry.IsPractice) return "体验/练习副本不记录任务物件";
        if (dungeon.Run.HasPendingDeath || dungeon.Run.HasPendingSettlement || pendingQuestCompletion is not null) return "存在未完成的死亡/结算/任务完成操作";
        var snapshot = dungeon.Run.Snapshot;
        var room = dungeon.Route.Rooms.Single(row => row.Room.Map == snapshot.CurrentMap).Room;
        var same = room.PassiveObjects.Where(source => source.ObjectId == templateId).ToArray();
        if (same.Length == 0) return $"当前房间 {snapshot.CurrentMap.Id} 没有物件模板 {templateId}";
        if (same.Length > 1) return $"当前房间 {snapshot.CurrentMap.Id} 有 {same.Length} 份物件模板 {templateId}。39 与迷宫终末仍要求唯一行；猎杀 33 只消费尚未使用的放置行，已满、未接取或目标不匹配时拒绝";
        if (!resources.ReportsQuestDeath(same[0]) && !resources.ReportsQuestItemReward(same[0])) return $"物件模板 {templateId} 无 [quest type] [must be destroyed] 或 [reward type] [quest item]，仅在任务触发通道或迷宫清图条件下才会记录";
        return "条件未满足（任务未接取/未绑定本次入场/目标不匹配/等待中的事件指纹不同）";
    });
    public QuestLifecycleReceipt? ObserveFormalPassiveTrigger(ushort questId, QuestClientCondition condition,
        string fingerprint, DungeonRoomCatalog resources)
    {
        if (condition is not { Channel: 0x10 or 0x20 or 0x40, Value: 0, Reserved: 0 } || fingerprint.Length != 64 ||
            questCatalog is null || !questCatalog.TryGet(questId,out var quest) || !QuestHuntProgress.TryTargets(quest,out var targets))
            return null;
        int index = condition.Channel == 0x10 ? 0 : condition.Channel == 0x20 ? 1 : 2;
        if (index >= targets.Length || targets[index].EnemyType != 3) return null;
        // 匹配客户端被动物件演出也从0200F950发33；它不是普通怪物死亡，不要求另一个39报告。
        return ObserveFormalPassiveEvent(checked((uint)targets[index].MonsterId),fingerprint,resources,questId);
    }

    private QuestLifecycleReceipt? ObserveFormalPassiveEvent(uint templateId, string fingerprint,
        DungeonRoomCatalog resources, ushort? reportedQuest = null)
    {
        if (questCatalog is null) return null;
        var capture = owner.Execute(() =>
        {
            var dungeon = formalDungeon;
            var character = Ready && Selected is not null && Phase is SessionPhase.Dungeon or SessionPhase.Settlement && dungeon is not null &&
                !dungeon.Entry.IsClassExperience && !dungeon.Entry.IsPractice &&
                !dungeon.Ground.HasPendingOperation && (!dungeon.Run.HasPendingDeath || dungeon.Run.HasPendingPassiveClear &&
                    dungeon.QuestSceneEvents.Any(pair => pair.Key.Map == dungeon.Run.Snapshot.CurrentMap &&
                        pair.Key.Template == (int)templateId && !pair.Value.Committed)) &&
                !dungeon.Run.HasPendingSettlement && pendingQuestCompletion is null
                ? Selected : null;
            var snapshot = dungeon?.Run.Snapshot;
            var room = snapshot is null ? null : dungeon!.Route.Rooms.Single(row => row.Room.Map == snapshot.CurrentMap).Room;
            return (Character: character, SelectionEpoch, Phase, Dungeon: dungeon, Snapshot: snapshot, Room: room);
        });
        if (capture.Character is not { } expected || capture.Dungeon is not { } current || capture.Snapshot is not { } scene || capture.Room is null)
            return null;
        if (reportedQuest is { } questId && !expected.ActiveMainQuests.Any(active => active.QuestId == questId &&
            QuestHuntProgress.Bound(active,current.Entry))) return null;
        var matches = capture.Room.PassiveObjects.Select((source,ordinal) => (source,ordinal))
            .Where(row => row.source.ObjectId == templateId).ToArray();
        if (matches.Length == 0) return null;
        // 39 与迷宫终末仍要求本图唯一模板行。猎杀 33 不带实例；0200F950 在活动 scalar 非 0 时，每次匹配销毁都发送同一条通道 33。
        // 按本图放置行逐次消费，不超过放置份数。序号是服务端消费槽，不是报文里的实例号。
        PendingQuestScene? pending = null;
        int selected;
        if (matches.Length == 1)
            selected = matches[0].ordinal;
        else if (reportedQuest is null)
            return null;
        else
        {
            var rows = current.QuestSceneEvents.Where(pair => pair.Key.Map == scene.CurrentMap && pair.Key.Template == (int)templateId).ToArray();
            var open = rows.Where(pair => !pair.Value.Committed && pair.Value.Plan.SceneEvent?.Fingerprint == fingerprint).ToArray();
            if (open.Length == 1)
            {
                selected = open[0].Key.Ordinal;
                pending = open[0].Value;
            }
            else if (rows.Any(pair => !pair.Value.Committed))
                return null;
            else
            {
                var used = rows.Select(pair => pair.Key.Ordinal).ToHashSet();
                selected = matches.Select(row => row.ordinal).FirstOrDefault(ordinal => !used.Contains(ordinal), -1);
                if (selected < 0) return null;
            }
        }
        if (matches.Length == 1)
            current.QuestSceneEvents.TryGetValue((scene.CurrentMap, (int)templateId, selected), out pending);
        if (pending is { } observed && (observed.Plan.SceneEvent!.Fingerprint != fingerprint || observed.Committed)) return null;
        var chosen = matches.Single(row => row.ordinal == selected);
        var key = (scene.CurrentMap, (int)templateId, selected);
        bool reportsQuestDeath = resources.ReportsQuestDeath(chosen.source);
        bool recordsQuest = reportsQuestDeath || resources.ReportsQuestItemReward(chosen.source);
        bool mazeClear = current.Run.IsPassiveClearCondition(scene.Identity, (int)templateId, chosen.ordinal);
        if (pending is null && mazeClear && !expected.ActiveMainQuests.Any(active => active.QuestId == current.Entry.RouteQuestId &&
            QuestHuntProgress.Bound(active,current.Entry))) return null;
        if (pending is null && reportedQuest is null && !recordsQuest &&
            (!mazeClear || current.Run.ClearFact is not null || !current.Entry.IsTerminalMap(scene.CurrentMap))) return null;
        var plan = pending?.Plan;
        if (plan is null)
        {
            var source = new QuestSceneEvent(Guid.NewGuid(),scene.Identity,checked((int)current.Entry.DungeonId),scene.CurrentMap.Id,
                checked((int)templateId),chosen.ordinal,fingerprint);
            bool clearCandidate = owner.Execute(() => ReferenceEquals(Selected,expected) && ReferenceEquals(formalDungeon,current) &&
                SelectionEpoch == capture.SelectionEpoch && current.Run.CanPreparePassiveClear(scene.Identity,source.TemplateId,source.SourceOrdinal));
            if (reportedQuest is null && !recordsQuest && !clearCandidate) return null;
            var drops = current.QuestDrops is { } questDrops ? QuestDropPlan.Create(expected,current.Run.Entry,questDrops,
                source.OperationId,scene.Identity,scene.CurrentMap.Id,checked((int)templateId),3,clearCandidate,current.Draw,DateTimeOffset.UtcNow) : null;
            plan = owner.Execute(() =>
            {
                if (!Ready || Phase != capture.Phase || SelectionEpoch != capture.SelectionEpoch || !ReferenceEquals(Selected,expected) ||
                    !ReferenceEquals(formalDungeon,current) || current.Run.Snapshot.Identity != scene.Identity) return null;
                var prepared = mazeClear ? current.Run.PreparePassiveClear(scene.Identity,source.OperationId,
                    source.TemplateId,source.SourceOrdinal) : null;
                if (reportedQuest is null && !recordsQuest && prepared is null) return null;
                try
                {
                    var candidate = QuestLifecyclePlan.ObservePassive(expected,questCatalog,current.Run.Entry,source,drops,prepared,current.Run);
                    if (candidate is null && prepared is not null) current.Run.CancelUncommittedPassiveClear(prepared);
                    return candidate;
                }
                catch
                {
                    // 此处尚未进入任何存储调用；规划失败可以撤销原准备，SQL结果不明则保留原候选。
                    if (prepared is not null) current.Run.CancelUncommittedPassiveClear(prepared);
                    throw;
                }
            });
        }
        if (plan is null) return null;
        return owner.Execute(() =>
        {
            if (!Ready || SelectionEpoch != capture.SelectionEpoch || Phase != capture.Phase ||
                !ReferenceEquals(formalDungeon,current) || current.Run.Snapshot.Identity != scene.Identity ||
                !ReferenceEquals(Selected,expected) || !ReferenceEquals(plan.Expected,expected))
                throw new InvalidOperationException("被动物件规划的原角色或实际房间已失效");
            var stored = pending ?? new PendingQuestScene(plan);
            current.QuestSceneEvents[key] = stored;
            bool clearCondition = current.Run.IsPassiveClearCondition(scene.Identity, (int)templateId, plan.SceneEvent!.SourceOrdinal);
            var commit = repository.CommitQuestLifecycle(owner,plan);
            if (commit.ProjectionCurrent)
            {
                Selected = commit.Character;
                stored.Committed = true;
                if (plan.SceneClear is { } prepared)
                {
                    var result = current.Run.ConfirmCommittedPassiveClear(prepared);
                    if (result.Outcome != DungeonRunOutcome.Accepted)
                        throw new InvalidOperationException("已提交场景清图的原实例准备无法确认");
                    SetState(SessionPhase.Settlement);
                }
                else if (clearCondition)
                {
                    var result = current.Run.ConfirmCommittedPassiveObject(scene.Identity, plan.OperationId,
                        (int)templateId, plan.SceneEvent.SourceOrdinal);
                    if (result.Outcome != DungeonRunOutcome.Accepted)
                        throw new InvalidOperationException("已提交物件事件的原实例条件无法确认");
                    SetState(current.Run.Phase == SinglePlayerDungeonPhase.Settlement ? SessionPhase.Settlement : SessionPhase.Dungeon);
                }
            }
            return new QuestLifecycleReceipt(commit.Character,commit,owner.Generation,SelectionEpoch,Phase,current.Run.Snapshot.Identity,
                "真实地图的被动物件事件已提交") { Outcome = QuestLifecycleOutcome.Committed };
        });
    }
}
