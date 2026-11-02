using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class FatigueAccelerationHandler(ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.FATIGUE_ACCELERATION_STATE_CHANGE,
        ChangeAsync, nameof(FatigueAccelerationHandler));

    async Task<CommandResult> ChangeAsync(GameConnectionContext context, Packet packet)
    {
        bool enabled = FatigueAccelerationRequests.Change(packet.Body);
        var receipt = context.Session.ChangeFatigueAcceleration(enabled, context.BlocksTownActions);
        bool Guard() => receipt is not null && context.Session.CanPublishFatigueAcceleration(receipt);
        ushort error = receipt?.Error ?? (ushort)19;
        log.Write(error == 0 ? "fatigue_acceleration" : "fatigue_acceleration_rejected",
            new { Connection = context.Id, Character = receipt?.CharacterId, Enabled = enabled, Accepted = error == 0, Error = error });
        await context.Reply(CmdPacketType.FATIGUE_ACCELERATION_STATE_CHANGE, FatigueAccelerationPackets.Ack(error),
            error == 0 ? "fatigue_acceleration_saved" : "fatigue_acceleration_rejected", receipt is null ? null : Guard);
        if (receipt is { Error: 0 }) await context.Notify(NotiPacketType.USER_FATIGUE_ACCELERATION_STATE,
            FatigueAccelerationPackets.State(receipt.Active), "fatigue_acceleration_state",
            () => context.Session.CanPublishFatigueAcceleration(receipt, requireCurrentState: true));
        return CommandResult.Handled;
    }
}
