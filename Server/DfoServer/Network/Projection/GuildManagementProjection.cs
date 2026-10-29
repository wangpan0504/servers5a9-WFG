using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class GuildManagementProjection(GuildManagementService service, GuildPanelService panels,
    TownPresence? presence = null)
{
    public Action<GameConnectionContext>? AccountRefresh { get; set; }
    public GuildDungeonProjection? Dungeons { get; set; }
    public void Post(GameConnectionContext context, GuildManagementNotice notice)
    {
        if (notice.Kind == GuildManagementNoticeKind.AccountRefresh)
        {
            AccountRefresh?.Invoke(context);
            return;
        }
        if (notice.Kind == GuildManagementNoticeKind.Invite && notice.Invitation is { } invite)
        {
            context.Sender.Post(0, (ushort)NotiPacketType.CALL_GUILD_INVITE_TO_INVITED,
                GuildManagementPackets.Invitation(invite), "guild_invitation",
                () => !invite.Expired && invite.Target.IsCurrent() && invite.Inviter.IsCurrent());
            return;
        }
        if (notice.Kind == GuildManagementNoticeKind.InvitationResult)
        {
            context.Sender.Post(0, (ushort)NotiPacketType.REPLY_GUILD_INVITE_TO_CALLER,
                GuildManagementPackets.InvitationResult(notice.Result, notice.Name), "guild_invitation_result",
                notice.Invitation?.Inviter.IsCurrent);
            return;
        }
        if (context.Session.RefreshGuildManagement(service.Store, notice.GuildId) is not { } receipt) return;
        GuildDungeonPartyBuffProjection.Post(context);
        var character = receipt.Character;
        bool Current() => receipt.IsCurrent();
        if (receipt.EvacuatedHall)
        {
            context.Owner.Execute(() => { if (Current()) presence?.Remove(context.Sender); return true; });
            using (context.Sender.QueuePublication(Current)) context.Sender.ClearScene().GetAwaiter().GetResult();
            context.Sender.Post(0, (ushort)NotiPacketType.USER_AREA,
                TownPackets.Position(character, true), "guild_hall_departure", Current);
            context.Sender.Post(0, (ushort)NotiPacketType.AREA_USERS,
                TownPackets.Transition(character), "guild_hall_departure_transition", Current);
            context.Owner.Execute(() => { if (Current()) presence?.Join(context.Owner, context.Sender, character, context.Channel,
                context.Session.ReadAdventureName(), pvpGrade: context.Session.ReadPvpGrade(character)); return true; });
        }
        if (receipt.State is null)
            context.Sender.Post(0, (ushort)NotiPacketType.GUILD_SECEDE_TO_USER, [], "guild_membership_cleared", Current);
        else if (receipt.Joined)
            context.Sender.Post(0, (ushort)NotiPacketType.GUILD_ENTER,
                GuildPackets.Entered(receipt.State.Guild.Id, character.Id, receipt.State.Guild.Name, character.Name), "guild_joined", Current);
        // 资格立即安装；场景中的人物投影仅在城镇发送，副本成员不会被误判为离线。
        if (receipt.ActorChanged && context.Session.Phase == SessionPhase.Town)
        {
            context.Sender.Post(0, (ushort)NotiPacketType.USERINFO,
                TownPackets.Actor(character, context.Session.ReadPvpGrade(character), context.Channel, adventureName: context.Session.ReadAdventureName(),
                    premiumPcRoom: context.Session.PremiumPcRoom), "guild_member_actor",
                () => Current() && context.Session.Phase == SessionPhase.Town);
            context.Owner.Execute(() =>
            {
                if (Current()) presence?.Refresh(context.Owner, context.Sender, character, pvpGrade: context.Session.ReadPvpGrade(character));
                return true;
            });
        }
        // 127带有会籍刷新副作用；普通公告/积分/成员查询不能重放，且本人Actor须先完成安装。
        if (receipt.GradeChanged || receipt.ActorChanged && context.Session.Phase == SessionPhase.Town)
            context.Sender.Post(0, (ushort)NotiPacketType.GUILD_MEMBER_INFO, [receipt.State?.Grade ?? (byte)100], "guild_member_grade", Current);
        if (receipt.State is not { } state || panels.Read(context.Owner, character.Id, context.Channel.ServerIndex) is not { } panel)
        {
            AccountRefresh?.Invoke(context);
            return;
        }
        context.Sender.Post(0, (ushort)NotiPacketType.GUILD_INFO,
            GuildPackets.Information(state.Guild, context.Channel.ServerIndex, panel), "guild_management_information", Current);
        for (int offset = 0; offset < panel.Members.Length || offset == 0; offset += 128)
        {
            int count = Math.Min(128, panel.Members.Length - offset);
            context.Sender.Post(1, (ushort)(offset == 0 ? CmdPacketType.GUILD_ALL_MEMBER_LIST : CmdPacketType.GUILD_ALL_MEMBER_LIST_NEXT),
                GuildPackets.Members(panel, offset, count, offset == 0, true), "guild_management_members", Current);
        }
        AccountRefresh?.Invoke(context);
        // 后续贡献刷新会更换发布回执；每次都携带最新进度，避免队列中的旧744失效后没有替代通知。
        Dungeons?.Post(context, panel, Current);
    }
}
