using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;
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
            if (rooms is not null) changed = context.Session.ObserveFormalPassiveTrigger(request.QuestId,condition,
                Convert.ToHexString(SHA256.HashData(packet.Body)),rooms);
            changed ??= context.Session.ChangeQuest(request.QuestId, QuestLifecycleAction.Condition, quests, items, goldLimits, pieces,condition);
            if (changed is null) return CommandResult.Unhandled;
            if (changed.Commit is { ProjectionCurrent: false }) return CommandResult.CloseConnection;
            log.Write("quest_condition", new { Connection = context.Id, request.QuestId, changed.Outcome, changed.Reason });
        }
        uint scalar = clear is not null ? 0 : changed!.Character.ActiveMainQuests.SingleOrDefault(active => active.QuestId == request.QuestId)?.ConditionState ?? 1;
        QuestCompletionReceipt? closed = null;
        if (scalar == 0 && changed?.Outcome != QuestLifecycleOutcome.Rejected && quests is not null && parameters is not null &&
            items is not null && goldLimits is not null && pieces is not null && growth is not null &&
            quests.TryGet(request.QuestId,out var quest) && QuestRelationProgress.ZeroRewardChild(quest))
        {
            // 匹配V31子任务33后没有34；复用普通交付事务保存零奖励完成位和父任务进度。
            closed = context.Session.FinishQuest(request.QuestId,ushort.MaxValue,quests,parameters,items,goldLimits,pieces,growth).Receipt;
            if (closed is { Commit.ProjectionCurrent: false }) return CommandResult.CloseConnection;
            if (closed is not null) log.Write("quest_child_closed",new { Connection = context.Id, request.QuestId, closed.Commit.AcceptanceId, closed.Commit.Duplicate });
        }
        bool Guard() => closed is not null ? context.Session.CanPublishQuestCompletion(closed) :
            clear is not null ? context.Session.CanPublishQuestCondition(clear) : context.Session.CanPublishQuestLifecycle(changed!);
        byte[] body = changed?.Outcome == QuestLifecycleOutcome.Rejected ? QuestPackets.Rejected(19) : QuestPackets.ClearTrigger(request.QuestId,scalar);
        // 02353232的error19直接静默收尾；终末NPC条件仅ACK33，不全量刷新打断对话。
        await context.Reply(CmdPacketType.SET_QUEST_TRIGGER, body, "quest_condition_result", Guard);
        if (changed?.Commit?.Plan.Inventory.ChangedSlots is { Length: > 0 } slots)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,
                InventoryPackets.Delta((closed?.Commit.Character ?? changed.Character).Inventory,
                    InventoryListType.Main,[.. slots]),"quest_scene_inventory",Guard);
        if (closed is not null)
        {
            await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(closed.Commit.Character.ActiveMainQuests),"quest_child_parent_progress",Guard);
            await context.Notify(NotiPacketType.CLEAR_QUEST_LIST,TownPackets.CompletedQuests(closed.Commit.Character),"quest_child_completed",Guard);
        }
        return CommandResult.Handled;
    }
}
