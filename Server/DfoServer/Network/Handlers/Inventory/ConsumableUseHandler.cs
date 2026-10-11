using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class ConsumableUseHandler(InventoryConsumableUseService use, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.USE_STACKABLE, Handle, nameof(ConsumableUseHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var request = ConsumableUseRequests.Read(packet.Body);
        var result = context.Session.UseConsumable(request, use, context.Activity?.BlocksTownActions == true);
        bool accepted = result.Outcome == ConsumableUseOutcome.Committed;
        log.Write("consumable_use", new { Connection = context.Id, Item = request.ItemId, Slot = request.Position.Index,
            List = request.Position.List, Accepted = accepted, result.Reason, Character = result.Guard?.Character?.Id,
            Revision = result.Guard?.Character?.Revision, result.SourceUid });
        if (result.Outcome == ConsumableUseOutcome.CloseConnection) return CommandResult.CloseConnection;
        var guard = result.Guard ?? throw new InvalidOperationException("道具使用缺少原状态发布身份");
        bool CanSend() => context.Session.CanPublishConsumable(guard);
        await context.Reply(CmdPacketType.USE_STACKABLE, ConsumableUsePackets.Ack(request, accepted),
            accepted ? "consumable_use_committed" : "consumable_use_rejected", CanSend);
        // ACK44原生消费一次源数量；不推源14，但保留同事务更新的实际寻物任务条件。
        if (result.Inventory?.Receipt is { } receipt && receipt.PreviousQuests is { } old &&
            !old.SequenceEqual(receipt.Character.ActiveMainQuests))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(receipt.Character.ActiveMainQuests),
                "consumable_quest_conditions", CanSend);
        return CommandResult.Handled;
    }
}
