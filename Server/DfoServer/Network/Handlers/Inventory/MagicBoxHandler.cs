using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class MagicBoxHandler(InventoryMagicBoxService magicBox, InventoryProjection projection,
    OnlineSessionDirectory? online = null, ServerLog? log = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.USE_RANDOMBOX_ITEM, HandleSingle, nameof(MagicBoxHandler));
        commands.Register(CmdPacketType.USE_RANDOMBOX_ITEM_EXPAND, HandleExpand, nameof(MagicBoxHandler));
    }

    async Task<CommandResult> HandleSingle(GameConnectionContext context, Packet packet) =>
        await Handle(context, packet, false);

    async Task<CommandResult> HandleExpand(GameConnectionContext context, Packet packet) =>
        await Handle(context, packet, true);

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet, bool expand)
    {
        MagicBoxOpenRequest request;
        try
        {
            request = expand ? MagicBoxRequests.Expand(packet.Body) : MagicBoxRequests.Single(packet.Body);
        }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.USE_RANDOMBOX_ITEM, MagicBoxPackets.Failure(), "magic_box_malformed");
            return CommandResult.Handled;
        }
        MagicBoxReceipt? receipt = null;
        var result = context.BlocksTownActions ? InventoryResult.Reject(19) :
            context.Session.PlanAndCommitInventory(character => magicBox.Open(character, request, out receipt), 19);
        bool seriaBatch = expand && receipt?.BoxItemId == 2682272;
        CmdPacketType responseOpcode = seriaBatch ? CmdPacketType.USE_RANDOMBOX_ITEM_EXPAND : CmdPacketType.USE_RANDOMBOX_ITEM;
        byte[] response = !result.Accepted || receipt is null ? MagicBoxPackets.Failure() :
            seriaBatch ? MagicBoxPackets.OpenedSeriaExpand(receipt) : MagicBoxPackets.OpenedSingle(receipt);
        await context.Reply(responseOpcode, response, result.Accepted ? "magic_box_opened" : "magic_box_rejected");
        await projection.RefreshAsync(context, result);
        if (result.Accepted && result.Receipt is { } committed && receipt is { BroadcastRewards.Count: > 0 } && online is not null)
        {
            byte server = context.Channel.ServerIndex;
            if (context.Session.CaptureServerBroadcastGuard(server, committed) is { } current)
            {
                foreach (int itemId in receipt.BroadcastRewards)
                {
                    byte[] body = MagicBoxPackets.Broadcast(committed.Character.Name, receipt.BoxItemId, itemId);
                    int recipients = online.PostServerGroup(server, (ushort)NotiPacketType.SERVER_BROADCAST, body,
                        "magic_box_announcement", current);
                    log?.Write("magic_box_announcement", new { Connection = context.Id, Character = committed.Character.Id,
                        Server = server, receipt.BoxItemId, ItemId = itemId, Recipients = recipients,
                        Revision = committed.Character.Revision });
                }
            }
        }
        return CommandResult.Handled;
    }
}
