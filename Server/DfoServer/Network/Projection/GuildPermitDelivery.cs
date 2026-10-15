using DfoServer.Game.Guild;
using DfoServer.Network.Protocol;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Projection;

/// <summary>
/// 入城补投入口（顺序模式：创建者与被邀请方**不是同时在线**）。
///
/// 为什么是窄接口而不是直接注入 <see cref="GuildProjection"/>：装配顺序是
/// `GuildProjection` → `InventoryProjection` → `TownProjection`，让 TownProjection 依赖整个
/// GuildProjection 会形成构造环；而入城序列只需要「按角色补投一份邀请」这一件事。
/// </summary>
internal interface IGuildPermitDeliverer
{
    /// <summary>入城序列发完后调用：登记表里没有指向该角色的未过期申请时什么都不发。</summary>
    Task DeliverPendingAsync(GameConnectionContext context);
}

/// <summary>
/// `NOTI 0x2AD`（建会认证邀请）的唯一发送点：在线邀请（被邀请方当场在城镇）与入城补投都走这里，
/// opcode / 包体 / 动作名只写一次。
/// </summary>
internal sealed class GuildPermitDelivery(GuildPermitRegistry permits) : IGuildPermitDeliverer
{
    /// <summary>
    /// 被邀请方入城就绪后补投申请。守卫只认「这条连接当前选中的正是被邀请角色」：
    /// 入城序列结束时 <c>GameSession.MarkReady</c> 还没执行（它在 `EnterAsync` 返回之后），
    /// 因此这里不能像在线投递那样要求 Ready，否则这一包会被写入泵丢掉。
    /// </summary>
    public async Task DeliverPendingAsync(GameConnectionContext context)
    {
        if (context.Session.Selected is not { } invitee)
            return;
        if (permits.Find(invitee.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) is not { } permit)
            return;
        await NotifyInviteAsync(context, permit);
    }

    /// <summary>
    /// 入城补投用的发送入口：await 到真的写出，包序紧跟入城序列的最后一条（`NOTI 124`）。
    /// </summary>
    internal static Task NotifyInviteAsync(GameConnectionContext context, GuildPermit permit) =>
        context.Notify(NotiPacketType.REQUEST_GUILD_CREATE_PERMIT, GuildPackets.Invite(permit), "guild_permit_invite",
            () => context.Session.Selected?.Id == permit.InviteeCharacterId);

    /// <summary>
    /// 在线邀请用的非阻塞入口（被邀请方已在城镇，由 `GameSession.TryDeliverGuildPermit` 的 owner 锁内守卫先复核身份）。
    /// 文本与大区使用受理时冻结的完整申请，不能只发送大区字节。
    /// </summary>
    internal static void PostInvite(GameConnectionContext context, GuildPermit permit) =>
        context.Sender.Post(0, (ushort)NotiPacketType.REQUEST_GUILD_CREATE_PERMIT, GuildPackets.Invite(permit), "guild_permit_invite",
            () => context.Session.Ready && context.Session.Selected?.Id == permit.InviteeCharacterId);
}
