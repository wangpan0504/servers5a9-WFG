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
        var same = PassiveQuestSources(room,resources,true).Where(row => row.source.ObjectId == templateId).ToArray();
        if (same.Length == 0) return $"当前房间 {snapshot.CurrentMap.Id} 没有物件模板 {templateId}";
        int used = dungeon.QuestSceneEvents.Count(pair => pair.Key.Map == snapshot.CurrentMap && pair.Key.Template == (int)templateId && pair.Value.Committed);
        if (used == same.Length) return $"当前房间 {snapshot.CurrentMap.Id} 的物件模板 {templateId} 已消费全部 {used} 个资源槽";
        if (same.Length > 1 && !resources.ReportsQuestItemReward(same[0].source)) return $"当前房间 {snapshot.CurrentMap.Id} 有 {same.Length} 份物件模板 {templateId}，39 无任务物品声明且不能确定唯一来源";
        if (!resources.ReportsQuestDeath(same[0].source) && !resources.ReportsQuestItemReward(same[0].source)) return $"物件模板 {templateId} 无 [quest type] [must be destroyed] 或 [reward type] [quest item]，仅在任务触发通道或迷宫清图条件下才会记录";
        return "条件未满足（任务未接取/未绑定本次入场/目标不匹配/等待中的事件指纹不同）";
    });

    // 普通行保持原序号；特殊父物件占独立资源槽。此序号只标记服务端消费额度，不是39中的演员编号。
    private static IEnumerable<(DungeonRoomPassiveObject source,int ordinal)> PassiveQuestSources(
        DungeonRoomDefinition room,DungeonRoomCatalog resources,bool includeSpecialRewards)
    {
        for (int ordinal = 0; ordinal < room.PassiveObjects.Count; ordinal++)
            yield return (room.PassiveObjects[ordinal],ordinal);
        if (!includeSpecialRewards) yield break;
        foreach (var parent in room.SpecialObjects)
        {
            var source = new DungeonRoomPassiveObject(parent.ObjectId,parent.RawValues,parent.Path);
            if (resources.ReportsQuestItemReward(source))
                yield return (source,checked(room.PassiveObjects.Count + parent.SourceOrdinal));
        }
    }
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
        if (!CanMutatePartyDungeon || questCatalog is null) return null;
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
        bool questItemDrop = reportedQuest is null && current.QuestDrops is { } dropResources &&
            expected.ActiveMainQuests.Any(active => QuestHuntProgress.Bound(active,current.Entry) &&
                dropResources.Quests.TryGetValue(active.QuestId,out var quest) &&
                quest.Condition is "[seeking]" or "[seek n meet npc]" && QuestLifecyclePlan.ValidSeeking(quest) &&
                quest.Definition!.Drops.Any(drop => drop.Matches(current.Entry.DungeonId,current.Entry.Difficulty,(int)templateId,3,false)));
        var matches = PassiveQuestSources(capture.Room,resources,questItemDrop)
            .Where(row => row.source.ObjectId == templateId).ToArray();
        if (matches.Length == 0) return null;
        // 寻物39只在明确[quest item]声明下消费同模板的等价资源槽，额度来自MAP普通行与特殊父物件。
        // 同房完整正文只提交一次；跨房可以有相同正文，不能以Unknown39或正文指纹推断客户端实例身份。
        // 猎杀33沿用原活动scalar合同；迷宫清图仍由Run的唯一普通行门禁决定。
        PendingQuestScene? pending = null;
        int selected;
        var rows = current.QuestSceneEvents.Where(pair => pair.Key.Map == scene.CurrentMap && pair.Key.Template == (int)templateId).ToArray();
        if (reportedQuest is null && rows.Any(pair => pair.Value.Committed && pair.Value.Plan.SceneEvent?.Fingerprint == fingerprint))
            return null;
        if (matches.Length == 1)
            selected = matches[0].ordinal;
        else if (reportedQuest is null && (!questItemDrop || !resources.ReportsQuestItemReward(matches[0].source)))
            return null;
        else
        {
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
        if (pending is null && mazeClear && current.Entry.PartyRoute is null && !expected.ActiveMainQuests.Any(active => active.QuestId == current.Entry.RouteQuestId &&
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
