using DfoServer.Game.Town;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public TeleportResult TeleportPrivilege(PrivilegeTeleportRequest request, TeleportService service, bool blocked = false) =>
        PlanTeleport(character => service.Privilege(character, request, ChannelEntry,
            service.Clock.GetUtcNow().ToUnixTimeMilliseconds()), service.Clock, blocked);
}
