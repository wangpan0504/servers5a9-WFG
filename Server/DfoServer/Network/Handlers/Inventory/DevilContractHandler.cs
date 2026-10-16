using System.Buffers.Binary;
using DfoServer.Infrastructure;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class DevilContractHandler(ServerLog log, TimeProvider? clock = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.PREMIUM_SERVICE,
        Handle, nameof(DevilContractHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        // D95的026E56FD/026E6149：u16子型1 + 74B快照，快照首u16是服务编号。
        // 到期和次数只读服务端账本，客户端上传的其余72字节没有授予资格的权限。
        if (packet.Body.Length != 76 || BinaryPrimitives.ReadUInt16LittleEndian(packet.Body) != 1)
            return CommandResult.Unhandled;
        ushort slot = BinaryPrimitives.ReadUInt16LittleEndian(packet.Body.AsSpan(2));
        if (slot is not (3 or 4)) return CommandResult.Unhandled;
        var result = context.Session.ReportDevilEffect((byte)slot, (clock ?? TimeProvider.System).GetUtcNow());
        log.Write("devil_dungeon_effect", new { Connection = context.Id, Slot = slot, result.Accepted,
            result.Error, Revision = result.Receipt?.Character.Revision });
        // 原生客户端已经执行本地效果；回写服务器现状，绝不回传客户端提供的账本。
        await DungeonCardMembershipProjection.RefreshAsync(context, includeOverview: false);
        return CommandResult.Handled;
    }
}
