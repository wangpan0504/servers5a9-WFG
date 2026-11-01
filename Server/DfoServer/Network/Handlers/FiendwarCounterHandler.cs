using DfoServer.Game.Titles;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class FiendwarCounterHandler(IFiendwarCounterStore store, TitleAchievementService achievements,
    TitleProjection titles, InventoryProjection inventory) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.RegisterPrivate(FiendwarCounterRequests.Opcode, Report, nameof(FiendwarCounterHandler));

    async Task<CommandResult> Report(GameConnectionContext context, Packet packet)
    {
        var report = FiendwarCounterRequests.Parse(packet.Body);
        if (context.FiendwarCounterRoom is not { } room ||
            !context.Session.ReportFiendwarCounter(store, room.Receipt, room.Serial, report)) return CommandResult.Handled;
        var result = context.Session.UpdateTitleAchievements(achievements, new(7006, new(0, 0, 0)), fiendwarOnly: true);
        await titles.PublishAsync(context, result, inventory);
        return CommandResult.Handled;
    }
}
