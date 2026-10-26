using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class AvatarSocketHandler(InventoryAvatarSocketService service, InventoryProjection projection,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.ADD_AVATAR_SOCKET, Handle, nameof(AvatarSocketHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        AvatarSocketRequest request;
        try { request = AvatarSocketRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("avatar_socket", new { Connection = context.Id, Length = packet.Body.Length, Accepted = false, Reason = "malformed" });
            await context.Reply(CmdPacketType.ADD_AVATAR_SOCKET, AvatarSocketPackets.Failure(), "avatar_socket_malformed");
            return CommandResult.Handled;
        }
        var capture = context.Owner.Execute(() =>
            (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        string? rejection = null;
        var result = context.BlocksTownActions ? InventoryResult.Reject(InventoryAvatarSocketService.Rejection) :
            context.Session.PlanAndCommitInventory(character => service.Open(character, request, out rejection),
                InventoryAvatarSocketService.Rejection);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
                capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        log.Write("avatar_socket", new { Connection = context.Id, request.TargetSlot, request.TargetItemId, request.MaterialSlot,
            result.Accepted, Reason = result.Accepted ? "committed" : rejection ?? "state-or-snapshot", Revision = result.Receipt?.Character.Revision });
        if (!result.Accepted)
            await context.Reply(CmdPacketType.ADD_AVATAR_SOCKET, AvatarSocketPackets.Failure(), "avatar_socket_rejected", Guard);
        else
        {
            // ACK先完成原时装引用及两栏pending收尾，并扣源一次；之后才能重建目标，源不再重复投影。
            await context.Reply(CmdPacketType.ADD_AVATAR_SOCKET,
                AvatarSocketPackets.Success(request.TargetSlot, request.MaterialSlot), "avatar_socket_committed", Guard);
            await projection.RefreshAsync(context, result, acknowledgedSlots: new HashSet<InventorySlot> { request.Material });
        }
        return CommandResult.Handled;
    }
}
