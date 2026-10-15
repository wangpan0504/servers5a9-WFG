using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class AvatarDyeHandler(InventoryAvatarDyeService dyes, InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.USE_DYE, Handle, nameof(AvatarDyeHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        AvatarDyeRequest request;
        try { request = AvatarDyeRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("avatar_dye", new { Connection = context.Id, Length = packet.Body.Length, Accepted = false, Reason = "malformed" });
            await context.Reply(CmdPacketType.USE_DYE, AvatarDyePackets.Failure(), "avatar_dye_malformed");
            return CommandResult.Handled;
        }
        string? rejection = null;
        var result = context.BlocksTownActions ? InventoryResult.Reject(19) :
            context.Session.PlanAndCommitInventory(character => dyes.Apply(character, request, out rejection), 19);
        var committed = result.Receipt;
        var target = committed?.Index.Find(request.Target);
        log.Write("avatar_dye", new { Connection = context.Id, request.SourceSlot, request.AvatarSlot, result.Accepted,
            Reason = result.Accepted ? "committed" : rejection ?? "state-or-snapshot", Revision = committed?.Character.Revision,
            Color1 = target?.Avatar?.Color1, Color2 = target?.Avatar?.Color2 });
        if (!result.Accepted)
            await context.Reply(CmdPacketType.USE_DYE, AvatarDyePackets.Failure(), "avatar_dye_rejected");
        else
        {
            if (committed is null || target?.Avatar is null) throw new InvalidOperationException("漂染缺少已提交时装详情");
            // ACK498在原时装对象上更新RAW4；不发目标NOTI14重建窗口仍引用的对象。
            await projection.RefreshAsync(context, result, acknowledgedSlots: new HashSet<InventorySlot> { request.Target });
            await context.Reply(CmdPacketType.USE_DYE, AvatarDyePackets.Success(request.AvatarSlot, target.Avatar), "avatar_dyed",
                () => context.Session.CanPublishInventory(committed));
        }
        return CommandResult.Handled;
    }
}
