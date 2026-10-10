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

    public QuestLifecycleReceipt? ObserveFormalPassiveDeath(uint templateId, uint actorFlags, bool checksum,
        string fingerprint, DungeonRoomCatalog resources)
    {
        // Full03 +1754与构造参数/资源查询同源；此分支没有普通怪物实例key。
        if (templateId is 0 or > int.MaxValue || (actorFlags & 0x21) != 0x21 || !checksum || fingerprint.Length != 64 || questCatalog is null)
            return null;
        return ObserveFormalPassiveEvent(templateId,fingerprint,resources);
    }

    public QuestLifecycleReceipt? ObserveFormalPassiveTrigger(ushort questId, QuestClientCondition condition,
        string fingerprint, DungeonRoomCatalog resources)
    {
        if (condition is not { Channel: 0x10 or 0x20 or 0x40, Value: 0, Reserved: 0 } || fingerprint.Length != 64 ||
            questCatalog is null || !questCatalog.TryGet(questId,out var quest) || !QuestHuntProgress.TryTargets(quest,out var targets))
            return null;
        int index = condition.Channel == 0x10 ? 0 : condition.Channel == 0x20 ? 1 : 2;
        if (index >= targets.Length || targets[index].EnemyType != 3) return null;
        // V31被动物件演出也从0200F950发33；它不是普通怪物死亡，不要求另一个39报告。
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
                !dungeon.Ground.HasPendingPickup && !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement && pendingQuestCompletion is null
                ? Selected : null;
            var snapshot = dungeon?.Run.Snapshot;
            var room = snapshot is null ? null : dungeon!.Route.Rooms.Single(row => row.Room.Map == snapshot.CurrentMap).Room;
            var key = (snapshot?.CurrentMap!, (int)templateId);
            return (Character: character, SelectionEpoch, Phase, Dungeon: dungeon, Snapshot: snapshot, Room: room,
                Pending: dungeon?.QuestSceneEvents.GetValueOrDefault(key), Key: key);
        });
        if (capture.Character is not { } expected || capture.Dungeon is not { } current || capture.Snapshot is not { } scene || capture.Room is null)
            return null;
        if (reportedQuest is { } questId && !expected.ActiveMainQuests.Any(active => active.QuestId == questId &&
            QuestHuntProgress.Bound(active,current.Entry))) return null;
        if (capture.Pending is { } observed && (observed.Plan.SceneEvent!.Fingerprint != fingerprint || observed.Committed)) return null;
        var matches = capture.Room.PassiveObjects.Select((source,ordinal) => (source,ordinal))
            .Where(row => row.source.ObjectId == templateId).ToArray();
        // 标识只有模板时，重复模板行无法唯一寻址；不按接取目标或数量反猜物件身份。
        // 39的无key死亡报告要求资源声明；33已按活动任务指定模板，普通自动销毁物件也走该原生消费者。
        if (matches.Length != 1 || reportedQuest is null && !resources.ReportsQuestDeath(matches[0].source)) return null;
        var plan = capture.Pending?.Plan;
        if (plan is null)
        {
            var source = new QuestSceneEvent(Guid.NewGuid(),scene.Identity,checked((int)current.Entry.DungeonId),scene.CurrentMap.Id,
                checked((int)templateId),matches[0].ordinal,fingerprint);
            var drops = current.QuestDrops is { } prepared ? QuestDropPlan.Create(expected,current.Run.Entry,prepared,
                source.OperationId,scene.Identity,scene.CurrentMap.Id,checked((int)templateId),3,false,current.Draw,DateTimeOffset.UtcNow) : null;
            plan = QuestLifecyclePlan.ObservePassive(expected,questCatalog,current.Run.Entry,source,drops);
        }
        if (plan is null) return null;
        return owner.Execute(() =>
        {
            if (!Ready || SelectionEpoch != capture.SelectionEpoch || Phase != capture.Phase ||
                !ReferenceEquals(formalDungeon,current) || current.Run.Snapshot.Identity != scene.Identity ||
                !ReferenceEquals(Selected,expected) || !ReferenceEquals(plan.Expected,expected))
                throw new InvalidOperationException("被动物件规划的原角色或实际房间已失效");
            var pending = capture.Pending ?? new PendingQuestScene(plan);
            current.QuestSceneEvents[capture.Key] = pending;
            var commit = repository.CommitQuestLifecycle(owner,plan);
            Selected = commit.Character; pending.Committed = true;
            return new QuestLifecycleReceipt(commit.Character,commit,owner.Generation,SelectionEpoch,Phase,scene.Identity,
                "真实地图的被动物件事件已提交") { Outcome = QuestLifecycleOutcome.Committed };
        });
    }
}
