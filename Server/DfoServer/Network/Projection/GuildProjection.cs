using DfoServer.Game.Guild;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection.Inventory;

namespace DfoServer.Network.Projection;

internal sealed class GuildProjection(InventoryProjection inventory, TownPresence? presence = null, GuildPanelService? panels = null)
{
    public void Notice(GameConnectionContext context, GuildPermitNotice notice)
    {
        if (notice.Kind == GuildPermitNoticeKind.Invite)
            GuildPermitDelivery.PostInvite(context, notice.Permit);
        else
            context.Sender.Post(0, (ushort)NotiPacketType.CANCEL_GUILD_CREATE, [], "guild_permit_cancelled",
                () => context.Session.Ready && context.Session.Selected?.Id == notice.Permit.InviteeCharacterId);
    }

    public async Task ResolvedAsync(GameConnectionContext context, GuildPermitOutcome outcome)
    {
        ushort initiator = outcome.InitiatorCharacterId;
        bool CanSend() => context.Session.Ready && context.Session.Selected?.Id == initiator;
        if (outcome.Inventory is { } change)
            await inventory.RefreshAsync(context, change);
        await context.Notify(NotiPacketType.REPLY_GUILD_CREATE_PERMIT, [outcome.Flag], "guild_permit_reply", CanSend);
        if (outcome.Flag != 1 || context.Session.CaptureGuildMembership(initiator) is not { } receipt)
            return;
        var guild = receipt.Character.Guild!;
        await context.Notify(NotiPacketType.GUILD_CREATE, GuildPackets.Created(guild.Id, guild.Name), "guild_created",
            () => context.Session.CanPublishGuildMembership(receipt));
        await JoinedAsync(context, receipt);
    }

    public async Task JoinedAsync(GameConnectionContext context, GuildMembershipReceipt receipt)
    {
        var character = receipt.Character;
        var guild = character.Guild ?? throw new InvalidOperationException("入会回执缺少已提交的公会");
        bool CanSend() => context.Session.CanPublishGuildMembership(receipt);
        // 58的第一个u32才是本人的公会ID；两边都要收到，不能只通知创建者。
        await context.Notify(NotiPacketType.GUILD_ENTER,
            GuildPackets.Entered(guild.Id, character.Id, guild.Name, character.Name), "guild_enter", CanSend);
        await context.Notify(NotiPacketType.USERINFO,
            TownPackets.Actor(character, context.Session.ReadPvpGrade(character), context.Channel, adventureName: context.Session.ReadAdventureName(),
                premiumPcRoom: context.Session.PremiumPcRoom, returningAdventurerType: context.Session.ReturningAdventurerType), "guild_actor", CanSend);
        await context.Notify(NotiPacketType.GUILD_MEMBER_INFO, [guild.Grade], "guild_member_grade", CanSend);
        await context.Notify(NotiPacketType.GUILD_INFO, GuildPackets.Information(guild, context.Channel.ServerIndex,
            panels?.Read(context.Owner, character.Id, context.Channel.ServerIndex)), "guild_information", CanSend);
        context.Owner.Execute(() =>
        {
            if (CanSend()) presence?.Refresh(context.Owner, context.Sender, character, pvpGrade: context.Session.ReadPvpGrade(character));
            return true;
        });
    }
}
