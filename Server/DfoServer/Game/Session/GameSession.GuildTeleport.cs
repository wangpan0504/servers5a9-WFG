using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Town;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public Action<GuildTeleportStatus>? GuildTeleportStatusArrived { private get; set; }
    public Func<TownTravelReceipt, Task>? GuildTeleportTravelArrived { private get; set; }
    public Func<bool>? GuildTeleportBlocked { private get; set; }

    public GuildTeleportPeer? CaptureGuildTeleport(byte slot)
    {
        try
        {
            return owner.Execute<GuildTeleportPeer?>(() =>
            {
                if (!CanGuildTeleport() ||
                    CaptureGuildManagement() is not { } identity || GuildTeleportStatusArrived is not { } status ||
                    GuildTeleportTravelArrived is not { } travel) return null;
                return new(identity, slot, status, travel, GuildRequestHighWatermark);
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }
    public Character? PrepareGuildTeleport(GuildManagementIdentity identity) => owner.Execute(() =>
        identity.IsCurrent() && CanGuildTeleport() ? Selected : null);
    public bool IsGuildTeleportCurrent(GuildTeleportPosition position) => owner.Execute(() =>
        position.Peer.Identity.IsCurrent() && CanGuildTeleport() &&
        ReferenceEquals(Selected, position.Expected));
    public void InstallGuildTeleport(GuildTeleportPosition position, Character committed) => owner.Execute(() =>
    {
        if (!IsGuildTeleportCurrent(position) || committed.Id != position.Expected.Id || committed.Position != position.Destination ||
            committed.Revision != position.Expected.Revision + 1) throw new InvalidOperationException("公会传送提交回执归属不符");
        Selected = committed; InvalidateImageCommunication(); InvalidateExpertJobStore();
        return true;
    });

    // 与参考服保持一致：决斗场和超时空频道不能借公会传送离开频道区域。
    bool CanGuildTeleport() => CanTeleport() && GuildTeleportBlocked?.Invoke() != true &&
        ChannelEntry?.Type is not (8 or 24 or ChannelAreaLanding.FiendwarType) && Selected is { IsPvpPrivate: false };
}
