using DfoServer.Game.Pvp;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
using System.Collections.Immutable;

namespace DfoServer.Network.Projection;

internal static class PvpArenaProjection
{
    public static void Notice(GameConnectionContext context, PvpArenaNotice notice,
        DfoServer.Game.Characters.CharacterStatComputer? stats = null)
    {
        bool Current() => notice.Departure is { } departure
            ? context.Session.CanPublishPvpArenaDeparture(departure)
            : notice.Recipient.IsCurrent && (notice.Kind != PvpArenaNoticeKind.ResultReturned ||
                notice.Recipient.Room.Match is null && notice.Recipient.Room.Revision == notice.View.Revision) &&
                (notice.Kind is not (PvpArenaNoticeKind.Start or PvpArenaNoticeKind.Loaded or
                PvpArenaNoticeKind.Connected or PvpArenaNoticeKind.Death or PvpArenaNoticeKind.TotalSelected or
                PvpArenaNoticeKind.TotalProgress) || notice.Recipient.Room.Match?.Id == notice.View.MatchId);
        if (!Current()) return;
        void Post(NotiPacketType opcode, byte[] body, string action) =>
            context.Sender.Post(0, (ushort)opcode, body, action, Current);
        var characters = notice.Characters.OrderBy(identity =>
            notice.View.Members.Single(row => row.CharacterId == identity.Character.Id).Seat).ToImmutableArray();

        if (notice.Departure is not null)
        {
            if (notice.Kind == PvpArenaNoticeKind.MatchingCancelled)
                Post(NotiPacketType.INTEGRATE_MATCHING_STATE, PvpMatchingPackets.LoadingState(3), "pvp_matching_loading_cancelled");
            Post(NotiPacketType.PVP_SEAT_STATE, PvpArenaPackets.Left(notice.View, notice.Departure.Seat), "pvp_arena_left");
            return;
        }
        // 只补新进入的角色，不能在换席和离房时重建已安装的角色引用。
        if (notice.Kind is PvpArenaNoticeKind.Room or PvpArenaNoticeKind.Seats or PvpArenaNoticeKind.Matching)
            foreach (var identity in characters)
                Post(NotiPacketType.USERINFO, TownPackets.Actor(identity.SkillCharacter, identity.Channel,
                    adventureName: identity.AdventureName, premiumPcRoom: identity.PremiumPcRoom), "pvp_arena_actor");
        switch (notice.Kind)
        {
            case PvpArenaNoticeKind.Matching:
                if (notice.MatchingRatings.Count != notice.Characters.Length || notice.Characters.Length != 2)
                    throw new InvalidOperationException("普通匹配初始化缺少双方真实评分");
                Post(NotiPacketType.PVP_ROOM_INFO, PvpArenaPackets.Room(notice.View), "pvp_matching_room");
                context.Sender.Post(1, (ushort)CmdPacketType.ENTER_PVP_ROOM, PvpArenaPackets.Entered(notice.View),
                    "pvp_matching_entered", Current);
                var players = characters.Select(identity => new PvpMatchingPlayer(identity.Character.Id,
                    notice.View.Members.Single(row => row.CharacterId == identity.Character.Id).Seat,
                    checked((uint)notice.MatchingRatings[identity.Character.Id].State.Rating), 0, 0, 0)).ToImmutableArray();
                Post(NotiPacketType.INTEGRATE_MATCHING_PLAYER_UID_LIST,
                    PvpMatchingPackets.Players(notice.View.IsTotal ? (byte)8 : (byte)2, notice.MatchingId, players,
                        notice.TotalRosters, notice.MatchingRatings), "pvp_matching_players");
                Post(NotiPacketType.INTEGRATE_MATCHING_COMPLETE, PvpMatchingPackets.BeginLoading(), "pvp_matching_preload");
                break;
            case PvpArenaNoticeKind.Start:
                if (notice.Recipient.Room.IsMatching)
                    Post(NotiPacketType.INTEGRATE_MATCHING_STATE, PvpMatchingPackets.LoadingState(2), "pvp_matching_ready");
                if (notice.View.IsTotal)
                {
                    Post(NotiPacketType.PVP_TOTAL_MATCH_INFO, PvpTotalPackets.Summaries(notice.TotalRosters, notice.MatchingRatings), "pvp_total_summaries");
                    Post(NotiPacketType.PVP_TOTAL_MATCH_CHARACTER_INFO, PvpTotalPackets.Actors(notice.TotalRosters,
                        (stats ?? throw new InvalidOperationException("总力缺少属性目录")).Compute), "pvp_total_actors");
                }
                else foreach (var identity in characters)
                        Post(NotiPacketType.USERINFO, TownPackets.Player(identity.SkillCharacter,
                            (stats ?? throw new InvalidOperationException("正式决斗缺少属性目录")).Compute(identity.SkillCharacter),
                            identity.AdventureLevel, identity.Channel), "pvp_arena_player");
                Post(NotiPacketType.USER_UDP_IP_PORT, PartyPackets.Endpoints(notice.Endpoints.ToArray()), "pvp_arena_start_endpoints");
                foreach (var member in notice.View.Members)
                    Post(NotiPacketType.PVP_READY_STATE, PvpArenaPackets.Ready(member.Seat, false), "pvp_arena_start_ready");
                Post(NotiPacketType.START_PVP, PvpArenaPackets.Start(notice.View), "pvp_arena_start");
                Post(NotiPacketType.PVP_ROOM_STATE, PvpArenaPackets.State(notice.View), "pvp_arena_started_state");
                break;
            case PvpArenaNoticeKind.Loaded:
                Post(NotiPacketType.COMPLETE_LOAD_PVP, PvpArenaPackets.Loaded(notice.Peer), "pvp_arena_loaded");
                if (notice.View.MatchPhase == PvpArenaMatchPhase.Fighting)
                    Post(NotiPacketType.CONNECT_P2P_PVP, [], "pvp_arena_connected_after_load");
                if (notice.View.IsTotal) TotalProgress();
                break;
            case PvpArenaNoticeKind.Connected:
                if (notice.View.MatchPhase == PvpArenaMatchPhase.Fighting)
                    Post(NotiPacketType.CONNECT_P2P_PVP, [], "pvp_arena_connected");
                break;
            case PvpArenaNoticeKind.Death:
                Post(NotiPacketType.DIE_PVP_CHARACTER, PvpArenaPackets.Death(notice.VictimSeat, notice.KillerSeat), "pvp_arena_death");
                if (notice.View.IsTotal) TotalProgress();
                break;
            case PvpArenaNoticeKind.TotalSelected:
                if (notice.View.MatchPhase == PvpArenaMatchPhase.Fighting) TotalProgress();
                break;
            case PvpArenaNoticeKind.TotalProgress:
                TotalProgress();
                break;
            case PvpArenaNoticeKind.Endpoints:
                Post(NotiPacketType.USER_UDP_IP_PORT, PartyPackets.Endpoints(notice.Endpoints.ToArray()), "pvp_arena_endpoints");
                break;
            case PvpArenaNoticeKind.Room:
                Post(NotiPacketType.PVP_ROOM_INFO, PvpArenaPackets.Room(notice.View), "pvp_arena_room");
                break;
            case PvpArenaNoticeKind.Seats:
                Post(NotiPacketType.PVP_SEAT_STATE, PvpArenaPackets.SeatList(notice.View), "pvp_arena_seats");
                foreach (var member in notice.View.Members)
                    Post(NotiPacketType.PVP_READY_STATE, PvpArenaPackets.Ready(member.Seat, member.Ready), "pvp_arena_seat_ready");
                break;
            case PvpArenaNoticeKind.State:
            case PvpArenaNoticeKind.ResultReturned:
                Post(NotiPacketType.PVP_ROOM_STATE, PvpArenaPackets.State(notice.View), "pvp_arena_state");
                Post(NotiPacketType.PVP_SEAT_STATE, PvpArenaPackets.SeatList(notice.View), "pvp_arena_mode_seats");
                // 模式或地图改变后，全房间的准备一并取消。
                foreach (var member in notice.View.Members)
                    Post(NotiPacketType.PVP_READY_STATE, PvpArenaPackets.Ready(member.Seat, member.Ready), "pvp_arena_ready_reset");
                break;
            case PvpArenaNoticeKind.Ready:
                var changed = notice.View.Members.Single(member => member.Seat == notice.ReadySeat);
                Post(NotiPacketType.PVP_READY_STATE, PvpArenaPackets.Ready(changed.Seat, changed.Ready), "pvp_arena_ready");
                break;
        }

        void TotalProgress()
        {
            bool TotalCurrent() => Current() && notice.Recipient.Room.Match?.Total?.Revision == notice.TotalRevision;
            if (notice.View.MatchPhase == PvpArenaMatchPhase.Selecting)
                context.Sender.Post(0, (ushort)NotiPacketType.START_SET_PVP_TOTAL_MATCH_CHARAC_ORDER, [], "pvp_total_select", TotalCurrent);
            else if (notice.View.MatchPhase == PvpArenaMatchPhase.Fighting)
                context.Sender.Post(0, (ushort)NotiPacketType.PVP_TOTAL_MATCH_FIGHT,
                    PvpMatchingPackets.RoundSelection(notice.TotalChoices.Select(row => new PvpTotalRoundSelection(row.OwnerCharacter, row.Index)).ToImmutableArray()),
                    "pvp_total_round", TotalCurrent);
        }
    }
}
