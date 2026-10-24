using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;

namespace DfoServer.Network.Handlers;

internal sealed class QuestLifecycleHandler(QuestCatalog quests, ItemCatalog items, GoldLimitCatalog goldLimits,
    EpicPieceCatalog pieces, ServerLog log, QuestProjection? projection = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.ACCEPT_QUEST, (context, packet) => ChangeAsync(context, packet, QuestLifecycleAction.Accept), nameof(QuestLifecycleHandler));
        commands.Register(CmdPacketType.GIVEUP_QUEST, (context, packet) => ChangeAsync(context, packet, QuestLifecycleAction.Giveup), nameof(QuestLifecycleHandler));
    }

    private async Task<CommandResult> ChangeAsync(GameConnectionContext context, Packet packet, QuestLifecycleAction action)
    {
        ushort questId;
        bool parsed = action == QuestLifecycleAction.Accept ? QuestRequests.TryAccept(packet.Body, out questId) : QuestRequests.TryGiveup(packet.Body, out questId);
        if (!parsed || context.Activity?.BlocksTownActions == true) return CommandResult.Unhandled;
        var receipt = context.Session.ChangeQuest(questId, action, quests, items, goldLimits, pieces);
        if (receipt is null) return CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishQuestLifecycle(receipt);
        var opcode = action == QuestLifecycleAction.Accept ? CmdPacketType.ACCEPT_QUEST : CmdPacketType.GIVEUP_QUEST;
        log.Write("quest_lifecycle", new
        {
            Connection = context.Id, questId, action, receipt.Outcome, receipt.Reason, receipt.Commit?.Duplicate,
            Accepted = receipt.Outcome != QuestLifecycleOutcome.Rejected, receipt.Phase,
            CharacterId = receipt.Character.Id, receipt.Character.Revision, receipt.Character.Level,
            receipt.Character.Job, receipt.Character.GrowType, ActiveQuestCount = receipt.Character.ActiveMainQuests.Length
        });
        if (receipt.Commit is not { } commit)
        {
            if (action == QuestLifecycleAction.Giveup && receipt.Outcome == QuestLifecycleOutcome.Unchanged)
            {
                await context.Reply(opcode, QuestPackets.GivenUp(questId), "quest_closed_child_confirmed", Guard);
                return CommandResult.Handled;
            }
            // Full03 ACK31/32的error18均跳过文案与正文；本服用它作中立业务拒绝。
            await context.Reply(opcode, QuestPackets.Rejected(18), "quest_lifecycle_rejected", Guard);
            // 只回放已完成的顺序子。NOTI356 末尾会调用顺序聚合，对第一个未完成子再发 C2S31。
            // 已接取的寻物末子若每次拒绝都补 356，客户端会立刻重发同一个 31，并把 30000 字节完成表打满主线程。
            if (action == QuestLifecycleAction.Accept && receipt.Outcome == QuestLifecycleOutcome.Rejected &&
                receipt.Character.CompletedQuests.Contains(questId) &&
                quests.TryGet(questId, out var rejected) && rejected.MainQuest is int parentId &&
                receipt.Character.ActiveMainQuests.Any(row => row.QuestId == parentId) &&
                quests.TryGet(parentId, out var parent) && parent.Condition == "[quest clear]" && parent.SubType == 2 &&
                parent.IntData.Contains(questId) && receipt.Character.ActiveMainQuests.Length != 0)
            {
                var refreshed = QuestInventoryProgress.Refresh(receipt.Character.ActiveMainQuests,receipt.Character.Inventory,quests,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),QuestEligibility.Completed(receipt.Character));
                await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(refreshed),
                    "quest_completed_child_active", Guard);
                await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(receipt.Character),
                    "quest_completed_child_history", Guard);
            }
            return CommandResult.Handled;
        }
        if (!commit.ProjectionCurrent) return CommandResult.CloseConnection;
        var plan = commit.Plan;
        var gifts = plan.Inventory.ChangedSlots.Select(slot => (Before: plan.Inventory.BeforeIndex.Find(slot), After: receipt.Character.Inventory.Find(slot)))
            .Where(pair => pair.After is not null && action == QuestLifecycleAction.Accept)
            .Select(pair => new QuestRewardItemProjection(pair.After!, pair.After!.Core.IsStackable ?
                pair.After.Core.Count - (pair.Before?.Core.Count ?? 0) : 1)).ToArray();
        byte[] body = action == QuestLifecycleAction.Accept
            ? QuestPackets.Accepted(questId, receipt.Character.ActiveMainQuests.Single(active => active.QuestId == questId).ConditionState, gifts)
            : QuestPackets.GivenUp(questId);
        await context.Reply(opcode, body, "quest_lifecycle_committed", Guard);
        if (plan.Inventory.ChangedSlots.Length != 0)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                DfoServer.Game.Inventory.Models.InventoryListType.Main, plan.Inventory.ChangedSlots.ToArray()), "quest_lifecycle_inventory", Guard);
        if (action == QuestLifecycleAction.Giveup && plan.Expected.ActiveMainQuests.Length - plan.ActiveAfter.Length > 1 ||
            plan.ActiveAfter.Any(row => plan.Expected.ActiveMainQuests.Any(old => old.AcceptanceId == row.AcceptanceId && old.ConditionState != row.ConditionState)))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(receipt.Character.ActiveMainQuests), "quest_giveup_children", Guard);
        if (projection is not null)
            await projection.RefreshAsync(context, receipt.Character, Guard, restoreCompleted: false);
        // 放弃父子任务产生空574时同样会清客户端完成集合，空存档也须恢复显示标记。
        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(receipt.Character), "quest_lifecycle_history", Guard);
        return CommandResult.Handled;
    }
}
