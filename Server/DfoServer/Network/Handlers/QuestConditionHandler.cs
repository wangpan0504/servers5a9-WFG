using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using System.Security.Cryptography;

namespace DfoServer.Network.Handlers;

internal sealed class QuestConditionHandler(ServerLog log, QuestCatalog? quests = null, ItemCatalog? items = null,
    GoldLimitCatalog? goldLimits = null, EpicPieceCatalog? pieces = null, DungeonRoomCatalog? rooms = null,
    QuestRewardParameterCatalog? parameters = null, CharacterGrowthCatalog? growth = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.SET_QUEST_TRIGGER, TriggerAsync, nameof(QuestConditionHandler));

    private async Task<CommandResult> TriggerAsync(GameConnectionContext context, Packet packet)
    {
        if (!QuestRequests.TryTrigger(packet.Body, out var request) || context.Activity?.BlocksTownActions == true)
            return CommandResult.Unhandled;
        QuestConditionReceipt? clear = null;
        QuestLifecycleReceipt? changed = null;
        if (request is { Channel: null, Value: 0, Reserved: 0 }) clear = context.Session.ReadClearedQuest(request.QuestId);
        if (clear is null)
        {
            if (quests is null || items is null || goldLimits is null || pieces is null) return CommandResult.Unhandled;
            var condition = new QuestClientCondition(request.Channel,request.Value,request.Reserved);
            changed = context.Session.ObserveAntonPhaseQuestTrigger(request.QuestId,condition,quests) ??
                context.Session.ObserveOperationHopeTrigger(request.QuestId,condition) ??
                context.Session.ObservePvpTrainingTrigger(request.QuestId,condition,quests);
            if (changed is null && rooms is not null && context.Session.PartyDungeon is { } party &&
                condition is { Channel: 0x10 or 0x20 or 0x40, Value: 0, Reserved: 0 } &&
                quests.TryGet(request.QuestId,out var sharedQuest) && QuestHuntProgress.TryTargets(sharedQuest,out var sharedTargets) &&
                (condition.Channel == 0x10 ? 0 : condition.Channel == 0x20 ? 1 : 2) is var targetIndex &&
                targetIndex < sharedTargets.Length && sharedTargets[targetIndex].EnemyType == 3)
            {
                using var peers = await PartyDungeonProjection.AcquirePeersAsync(context,party.Members);
                string fingerprint = Convert.ToHexString(SHA256.HashData(packet.Body));
                uint template = checked((uint)sharedTargets[targetIndex].MonsterId);
                var notices = party.ObservePassive(context.Session,template,fingerprint,
                    session => session.ObserveFormalPassiveTrigger(request.QuestId,condition,fingerprint,rooms) ??
                        session.ObserveFormalPassiveDeath(template,true,fingerprint,rooms),sequenced:true);
                changed = notices.SingleOrDefault(n => ReferenceEquals(n.Recipient.Actor.Session,context.Session))?.QuestScene;
                await PartyDungeonProjection.PublishAsync(notices.Where(n => !ReferenceEquals(n.Recipient.Actor.Session,context.Session)));
            }
            else if (changed is null && rooms is not null) changed = context.Session.ObserveFormalPassiveTrigger(request.QuestId,condition,
                Convert.ToHexString(SHA256.HashData(packet.Body)),rooms);
            if (changed is null && rooms is not null && quests.TryGet(request.QuestId,out var hunt) && QuestHuntProgress.TryTargets(hunt,out var huntTargets) &&
                condition.Channel is 0x10 or 0x20 or 0x40 && (condition.Channel == 0x10 ? 0 : condition.Channel == 0x20 ? 1 : 2) is var slot && slot < huntTargets.Length && huntTargets[slot].EnemyType == 3)
                log.Write("formal_passive_trigger_rejected", new { Connection = context.Id, request.QuestId, Template = huntTargets[slot].MonsterId,
                    Reason = context.Session.DiagnosePassiveEvent(checked((uint)huntTargets[slot].MonsterId),rooms) });
            changed ??= context.Session.ChangeQuest(request.QuestId, QuestLifecycleAction.Condition, quests, items, goldLimits, pieces,condition);
            if (changed is null) return CommandResult.Unhandled;
            if (changed.Commit is { ProjectionCurrent: false }) return CommandResult.CloseConnection;
            log.Write("quest_condition", new { Connection = context.Id, request.QuestId, changed.Outcome, changed.Reason });
        }
        uint scalar = clear is not null ? 0 : changed!.Character.ActiveMainQuests.SingleOrDefault(active => active.QuestId == request.QuestId)?.ConditionState ?? 1;
        if (changed is not null && scalar==0 && context.Session.PartyDungeon is { } cinematicParty &&
            quests is not null && quests.TryGet(request.QuestId,out var cinematicQuest) && QuestClientSceneProgress.DungeonCinematic(cinematicQuest))
        {
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context,cinematicParty.Members);
            await PartyDungeonProjection.PublishAsync(cinematicParty.CompleteCinematic(context.Session,request.QuestId,changed));
        }
        QuestCompletionReceipt? closed = null;
        bool parentCallback = false;
        if (changed?.Outcome != QuestLifecycleOutcome.Rejected && quests is not null && parameters is not null &&
            items is not null && goldLimits is not null && pieces is not null && growth is not null &&
            quests.TryGet(request.QuestId,out var quest))
        {
            // 原生NPC对话先确认子条件，再由父33或真实子34推进，不用574提前跳过对话。
            bool dialogue = quest.RewardType == "[item]" && QuestRelationProgress.TownLinkedChild(quest);
            if (scalar == 0 && QuestRelationProgress.ZeroRewardChild(quest) && !dialogue)
                closed = context.Session.FinishQuest(request.QuestId,ushort.MaxValue,quests,parameters,items,goldLimits,pieces,growth).Receipt;
            else if (changed is not null && request is { Channel: null, Value: 0, Reserved: 0 } &&
                QuestRelationProgress.SequentialParent(quest,quests))
            {
                parentCallback = true;
                closed = context.Session.FinishReadyQuestChildren(quests,parameters,items,goldLimits,pieces,growth,quest.Id);
                if (closed is not null)
                    scalar = closed.Commit.Character.ActiveMainQuests.Single(row => row.QuestId == request.QuestId).ConditionState;
            }
            if (closed is { Commit.ProjectionCurrent: false }) return CommandResult.CloseConnection;
            if (closed is not null) log.Write("quest_child_closed",new { Connection = context.Id, request.QuestId, closed.Commit.AcceptanceId, closed.Commit.Duplicate });
        }
        bool Guard() => closed is not null ? context.Session.CanPublishQuestCompletion(closed) :
            clear is not null ? context.Session.CanPublishQuestCondition(clear) : context.Session.CanPublishQuestLifecycle(changed!);
        byte[] body = changed?.Outcome == QuestLifecycleOutcome.Rejected ? QuestPackets.Rejected(19) : QuestPackets.ClearTrigger(request.QuestId,scalar);
        // 02353232的error19直接静默收尾；终末NPC条件仅ACK33，不全量刷新打断对话。
        await context.Reply(CmdPacketType.SET_QUEST_TRIGGER, body, "quest_condition_result", Guard);
        var committedSlots = closed is null ? [] : changed is null ? closed.Commit.ChangedSlots :
            new InventoryPlan(changed.Character.Inventory,closed.Commit.Character.Inventory,"子任务材料同步").ChangedSlots;
        var slots = (changed?.Commit?.Plan.Inventory.ChangedSlots ?? []).Concat(committedSlots)
            .Concat(closed?.Commit.ChangedSlots ?? []).Distinct().ToArray();
        if (slots.Length != 0)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,
                InventoryPackets.Delta((closed?.Commit.Character ?? changed!.Character).Inventory,
                    InventoryListType.Main,[.. slots]),"quest_scene_inventory",Guard);
        // 父回调仍不因普通对话子任务发574。范围子任务归档后活动表已经变了，ACK33之后必须补发574再356。
        if (closed is not null && (!parentCallback || closed.ArchivedTownRange))
        {
            await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(closed.Commit.Character.ActiveMainQuests),"quest_child_parent_progress",Guard);
            await context.Notify(NotiPacketType.CLEAR_QUEST_LIST,TownPackets.CompletedQuests(closed.Commit.Character),"quest_child_completed",Guard);
        }
        // 演出进度已保存但31尚未成功发布时，重复33的Unchanged也可沿Session原资格补发。
        if (changed?.Commit?.Plan.SceneEvent is not null ||
            request is { Channel: null, Value: 0, Reserved: 0 } && scalar == 0 &&
            changed?.Outcome is QuestLifecycleOutcome.Committed or QuestLifecycleOutcome.Unchanged &&
            quests is not null && quests.TryGet(request.QuestId,out var movie) && QuestClientSceneProgress.DungeonCinematic(movie))
            await DungeonSettlementProjection.EnableAsync(context);
        return CommandResult.Handled;
    }
}
