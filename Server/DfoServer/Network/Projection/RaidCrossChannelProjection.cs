using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class RaidCrossChannelProjection
{
    public static bool Request(OnlineSessionDirectory online, RaidRegistry registry, RaidCrossInvitation invitation,
        bool enableLuke, MarriageAppearance? marriage)
    {
        var source = invitation.Source;
        var target = invitation.Target;
        var connection = online.FindConnectionByCharacterId(target.Character);
        var identity = source.Session.PublicCharacter(source.Character);
        if (connection?.GuardedProbe is not { } send || !ReferenceEquals(connection.Session, target.Session) || identity is null)
            return false;
        var party = registry.Find(source);
        bool Guard() => registry.CanPublishCross(invitation) && identity.IsUnchanged() &&
            (party is null || registry.IsCurrent(party));
        var actor = TownPackets.Actor(identity.Character, identity.PvpGrade, identity.Channel,
            adventureName: identity.AdventureName, premiumPcRoom: identity.PremiumPcRoom, returningAdventurerType: identity.ReturningAdventurerType, marriage: marriage);
        if (!send((ushort)NotiPacketType.USERINFO, actor, "raid_cross_request_actor", Guard)) return false;
        if (party is not null && !send((ushort)NotiPacketType.RAID_LIST, RaidProjection.List(party, enableLuke),
            "raid_cross_request_directory", Guard)) return false;
        return send((ushort)NotiPacketType.RAID_OTHER_CHANNEL_REQUEST_JOIN,
            RaidCrossChannelPackets.Request(identity.Character, identity.Channel,
                source.Session.FatigueForClient(identity.Character).Remaining,
                StaminaRecoveryService.Percent(identity.Character.Inventory, DateTimeOffset.UtcNow), invitation.DisplayArgument, actor),
            "raid_cross_request", Guard);
    }

    public static bool Response(OnlineSessionDirectory online, RaidRegistry registry, RaidCrossTransfer transfer,
        bool enableLuke, MarriageAppearance? marriage)
    {
        var leader = transfer.Acceptance.Leader;
        var connection = online.FindConnectionByCharacterId(transfer.Entrant.Character);
        var identity = leader.Session.PublicCharacter(leader.Character);
        if (connection?.GuardedProbe is not { } send || !ReferenceEquals(connection.Session, transfer.Entrant.Session) || identity is null)
            return false;
        bool Guard() => registry.CanPublishCross(transfer) && identity.IsUnchanged();
        return send((ushort)NotiPacketType.USERINFO,
            TownPackets.Actor(identity.Character, identity.PvpGrade, identity.Channel, adventureName: identity.AdventureName,
                premiumPcRoom: identity.PremiumPcRoom, returningAdventurerType: identity.ReturningAdventurerType, marriage: marriage), "raid_cross_response_actor", Guard) &&
            send((ushort)NotiPacketType.RAID_LIST, RaidProjection.List(transfer.Party, enableLuke),
                "raid_cross_response_directory", Guard) &&
            send((ushort)NotiPacketType.RAID_OTHER_CHANNEL_RESPONSE_JOIN,
                RaidCrossChannelPackets.Response(leader.Character, leader.Channel), "raid_cross_response", Guard);
    }
}
