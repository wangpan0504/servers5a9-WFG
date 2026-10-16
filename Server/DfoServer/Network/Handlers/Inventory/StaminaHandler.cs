using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class StaminaHandler(StaminaRecoveryService service, InventoryProjection projection, ServerLog log,
    TimeProvider? clock = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.RECOVER_STAMINA, Handle, nameof(StaminaHandler));
    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        if (packet.Body.Length != 2) return CommandResult.Unhandled;
        ushort mode = BinaryPrimitives.ReadUInt16LittleEndian(packet.Body);
        var original = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
            context.Session.PlanAndCommitInventory(character => service.Recover(character, mode, (clock ?? TimeProvider.System).GetUtcNow()), 19);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            ReferenceEquals(context.Session.Selected, original.Selected) && context.Session.SelectionEpoch == original.SelectionEpoch && context.Session.Phase == original.Phase);
        // 本版023542D0成功消费一个u32金币值，并把本人虚弱置100；失败读公共u16错误码。
        byte[] body = new byte[result.Accepted ? 5 : 3];
        body[0] = result.Accepted ? (byte)1 : (byte)0;
        if (result.Accepted) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), result.Receipt!.Character.Inventory.Gold);
        else BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), result.Error);
        log.Write("stamina_recovery", new { Connection = context.Id, Mode = mode, result.Accepted, result.Error });
        await context.Reply(CmdPacketType.RECOVER_STAMINA, body, "stamina_recovery_committed", Guard);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
