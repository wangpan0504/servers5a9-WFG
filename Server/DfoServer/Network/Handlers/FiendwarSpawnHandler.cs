using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class FiendwarSpawnHandler : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.RegisterPrivate(FiendwarSpawnRequests.Opcode, Report, nameof(FiendwarSpawnHandler),
            sharedGate: static (context, _) => context.Session.CurrentParty?.Invoke()?.Commands);

    async Task<CommandResult> Report(GameConnectionContext context, Packet packet)
    {
        var report = FiendwarSpawnRequests.Parse(packet.Body);
        if (!report.Valid || context.FiendwarCounterRoom is not { } room || room.Serial != report.Serial ||
            !report.Matches(room.Receipt)) return CommandResult.Handled;
        var instance = context.Session.PartyDungeon;
        var party = context.Session.CurrentParty?.Invoke();
        if (instance is not null && (party is null || context.DispatchGates?.Holds(context, party.Commands) != true))
            return CommandResult.Handled;
        using var peers = instance is null ? null : await PartyDungeonProjection.AcquirePeersAsync(context, instance.Members);
        if (context.FiendwarCounterRoom != room || !ReferenceEquals(context.Session.PartyDungeon, instance) ||
            instance is not null && !ReferenceEquals(context.Session.CurrentParty?.Invoke(), party)) return CommandResult.Handled;
        _ = context.Session.ReportFiendwarSpawn(room.Receipt, room.Serial, report, instance);
        // FWS1只登记本次生成请求；没有原生成功ACK，不能伪造生成或死亡通知。
        return CommandResult.Handled;
    }
}
