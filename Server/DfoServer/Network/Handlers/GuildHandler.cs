using DfoServer.Game.Accounts;
using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

/// <summary>
/// 创建公会窗口的三次文本校验（CMD 0x9C 名称 / 0x9D 地址 / 0x2E3 介绍）与建会认证链路
/// （CMD 0x2E5 邀请 / 0x2E6 答复 / 0x2E7 关窗）。
///
/// 客户端契约（由客户端逆向 + 实机抓包确证）：校验应答走 **CMD 通道、与请求同号** 的 opcode，
/// 可用 = `01`（客户端据此解锁「创建」按钮），不可用 = `00 + u16LE 错误码`
/// （名称 0x6A 长度 / 0x6B 已被占用 / 0x6C 非法字符；地址 0x6E / 0x6F / 0x70）。
/// 判据是 `guilds.name` / `guilds.address` 的唯一索引，不另建内存去重表。
///
/// 认证链路的关键点：`0x2E5` **只带大区序号与目标角色名**（公会名/地址/介绍来自前两次校验，
/// 见 <see cref="GuildCreateDraft"/>）；被邀请方由 `NOTI 0x2AD` 弹窗、回 `CMD 0x2E6 [01]/[00]`；
/// 创建者的界面**完全由 `NOTI 0x2AE` 驱动**，建会成功再补 `NOTI 0x47`/`0x3A`。
///
/// 顺序登录模式兼容创建者与被邀请方先后上线：
/// `0x2E5` 不要求目标在线，成功回 `01` 后申请留在登记表里，被邀请方入城时由
/// <see cref="GuildPermitDelivery"/> 补投；接受时创建者已离线则用离线账号租约提交建会。
/// </summary>
internal sealed class GuildHandler(GuildDirectory directory, GuildPermitRegistry permits, OnlineSessionDirectory online,
    AccountOwners owners, ServerLog log, GuildProjection projection, GuildInvitations? invitations = null) : IGameCommandModule
{
    /// <summary>
    /// 申请有效期。客户端在被邀请方窗口停留 60 秒后**自动发拒绝**（`cmp eax,0xEA60`），而顺序模式下
    /// 创建者与被邀请方是**先后上线**：申请必须能等到被邀请方下次入城，因此不再按「同时在线」的
    /// 15 秒余量取值，改为与创建窗口草稿同寿命的 600 秒。
    /// 余量仍然保留：窗口出现后客户端自己的超时拒绝（最长 60 秒）总能匹配到申请。
    /// </summary>
    internal const int PermitLifetimeSeconds = 600;

    static readonly byte[] Available = [0x01];

    /// <summary>
    /// 目标角色不存在，或创建者账号仍在线但不是发起角色（客户端 0x2E5 文案「玩家不在线或者不存在。」、
    /// 0x2E6 文案「无法找到指定玩家」）。顺序模式下**目标离线不再是失败**。
    /// </summary>
    const ushort PlayerMissing = 3;

    /// <summary>目标与创建者同账号（客户端 0x2E5 文案「该角色与创建者属于相同账号。」）。</summary>
    const ushort SameAccount = 5;

    /// <summary>邀请目标已在公会，0x2E5错误表使用23。</summary>
    const ushort AlreadyInGuild = 23;

    // 0234ED30的答复错误表使用0x27（dstr21720），不能沿用邀请错误表的23。
    const ushort ReplyAlreadyInGuild = 39;

    /// <summary>
    /// 建会本身失败（无草稿、创建者已在公会、规划/提交被拒）。`0x2E5` 文案是「无法创建公会。」；
    /// `0x2E6` 的文案表里没有 34，被邀请方因此保持静默——**创建者在线**时的失败原因留在服务端日志。
    /// </summary>
    const ushort CreateFailed = 34;

    /// <summary>申请不存在或已过期（客户端 0x2E6 文案「公会邀请的等待时间已达到上限」）。</summary>
    const ushort PermitMissing = 103;

    /// <summary>被邀请方在挑战地下城（客户端 0x2AE 的 0x2D 分支文案「对方正在挑战地下城…」）。</summary>
    const byte InviteeBusy = 0x2D;

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.CHECK_GUILD_NAME_DOUBLE, CheckName, nameof(GuildHandler));
        commands.Register(CmdPacketType.CHECK_GUILD_ADDREASS_DOUBLE, CheckAddress, nameof(GuildHandler));
        commands.Register(CmdPacketType.CHECK_GUILD_CREATE_PROMOTE_MSG, CheckPromote, nameof(GuildHandler));
        commands.Register(CmdPacketType.REQUEST_GUILD_CREATE_PERMIT, RequestPermit, nameof(GuildHandler));
        commands.Register(CmdPacketType.REPLY_GUILD_CREATE_PERMIT, ReplyPermit, nameof(GuildHandler));
        commands.Register(CmdPacketType.CANCEL_GUILD_CREATE, CloseCreateWindow, nameof(GuildHandler));
    }

    Task<CommandResult> CheckName(GameConnectionContext context, Packet request) =>
        Reply(context, request, CmdPacketType.CHECK_GUILD_NAME_DOUBLE, "guild_name_check", 0x6A, 0x6B, 0x6C, directory.CheckName,
            permits.RememberName);

    Task<CommandResult> CheckAddress(GameConnectionContext context, Packet request) =>
        Reply(context, request, CmdPacketType.CHECK_GUILD_ADDREASS_DOUBLE, "guild_address_check", 0x6E, 0x6F, 0x70, directory.CheckAddress,
            permits.RememberAddress);

    /// <summary>
    /// 创建窗口「介绍」校验（CMD 0x2E3）。客户端处理器只在成功（flag≠0）时继续下一步——**自动发出认证邀请
    /// `0x2E5`**；flag=0 且错误码 0x6C 时弹「公会介绍包含非法字符。」，其它码静默。因此这里只回两种结果：
    /// 可用 `01`，或含控制字符时 `00 6C 00`。
    /// </summary>
    async Task<CommandResult> CheckPromote(GameConnectionContext context, Packet request)
    {
        byte[] body;
        try
        {
            string text = GuildRequests.Text(request.Body);
            var status = GuildTextRules.CheckPromote(text);
            if (status == GuildTextStatus.Available)
                Remember(context, text, permits.RememberPromote);
            body = status == GuildTextStatus.Illegal ? Error(0x6C) : Available;
        }
        catch (InvalidDataException)
        {
            // 正文宽度错误按非法字符回复，不伪造「可用」。
            body = Error(0x6C);
        }
        await context.Reply(CmdPacketType.CHECK_GUILD_CREATE_PROMOTE_MSG, body, "guild_promote_check");
        return CommandResult.Handled;
    }

    /// <summary>
    /// 认证邀请（CMD 0x2E5）。客户端不看成功应答（按钮状态在发包时本地改），但失败码是它自己的提示来源，
    /// 因此成功回 `01`、失败回 `00 + u16 码`。
    ///
    /// 顺序模式：目标**不要求在线**。目标当场在城镇时按既有路径立刻投递 `NOTI 0x2AD`（在线投递失败不伪造成功）；
    /// 目标不在线或当前选的是别的角色时只登记申请，回 `01`，等它入城时由入城补投送达（有效期
    /// <see cref="PermitLifetimeSeconds"/> 秒）。目标在线但不在城镇（副本/加载/结算）仍按客户端已有的
    /// `NOTI 0x2AE[0x2D]` 文案答复且**不登记申请**：入城补投只在入城序列后发生，登记一份当场投不出去的申请
    /// 会让创建者白等（被邀请方从副本回城不走入城序列）。
    /// </summary>
    async Task<CommandResult> RequestPermit(GameConnectionContext context, Packet request)
    {
        var (region, name) = GuildRequests.Permit(request.Body);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!context.Session.Ready || context.Session.Phase != SessionPhase.Town || context.Session.Selected is not { } creator)
        {
            await Reject(context, CmdPacketType.REQUEST_GUILD_CREATE_PERMIT, CreateFailed, "guild_permit_out_of_town", 0);
            return CommandResult.Handled;
        }
        var target = directory.FindPermitTarget(name);
        if (target is null)
        {
            await Reject(context, CmdPacketType.REQUEST_GUILD_CREATE_PERMIT, PlayerMissing, "guild_permit_target_missing", creator.Id);
            return CommandResult.Handled;
        }
        if (target.Account == context.Owner.Account)
        {
            await Reject(context, CmdPacketType.REQUEST_GUILD_CREATE_PERMIT, SameAccount, "guild_permit_same_account", creator.Id);
            return CommandResult.Handled;
        }
        if (target.InGuild)
        {
            await Reject(context, CmdPacketType.REQUEST_GUILD_CREATE_PERMIT, AlreadyInGuild, "guild_permit_target_in_guild", creator.Id);
            return CommandResult.Handled;
        }
        if (directory.InGuild(creator.Id))
        {
            await Reject(context, CmdPacketType.REQUEST_GUILD_CREATE_PERMIT, CreateFailed, "guild_permit_creator_in_guild", creator.Id);
            return CommandResult.Handled;
        }
        // 公会名/地址/介绍来自创建窗口的前两次校验：客户端先查重名称、再校验介绍，然后才自动发 0x2E5。
        var draft = permits.Draft(creator.Id, now);
        if (draft is null || draft.Name.Length == 0)
        {
            await Reject(context, CmdPacketType.REQUEST_GUILD_CREATE_PERMIT, CreateFailed, "guild_permit_draft_missing", creator.Id);
            return CommandResult.Handled;
        }
        var invitee = online.Find(target.Account);
        var state = invitee?.InviteeState(target.Id) ?? GuildInviteeState.Absent;
        if (state == GuildInviteeState.Busy)
        {
            // 在线但不在城镇：不弹认证窗口，用客户端为这种情形准备好的 0x2AE[0x2D] 文案答复，也不登记申请。
            await context.Reply(CmdPacketType.REQUEST_GUILD_CREATE_PERMIT, Available, "guild_permit_invitee_busy");
            await context.Notify(NotiPacketType.REPLY_GUILD_CREATE_PERMIT, [InviteeBusy], "guild_permit_invitee_busy");
            Log("invitee_busy", creator.Id, target.Id, draft.Name);
            return CommandResult.Handled;
        }

        var permit = new GuildPermit(region, draft.Name, draft.AddressOrName, draft.Promote, creator.Id, context.Owner.Account,
            target.Id, target.Account, now + PermitLifetimeSeconds) { InitiatorName = creator.Name };
        bool busy;
        lock (invitations?.SyncRoot ?? permits)
        {
            busy = invitations?.HasParticipant(context.Session) == true ||
                invitee is not null && invitations?.HasParticipant(invitee) == true;
            if (!busy) permits.Register(permit);
        }
        if (busy)
        {
            await Reject(context, CmdPacketType.REQUEST_GUILD_CREATE_PERMIT, PermitMissing, "guild_permit_consent_busy", creator.Id);
            return CommandResult.Handled;
        }
        if (state == GuildInviteeState.Ready)
        {
            if (!invitee!.TryDeliverGuildPermit(target.Id, new GuildPermitNotice(GuildPermitNoticeKind.Invite, permit)))
            {
                // 没有真的投递出去就不能留下「服务端认为已邀请」的申请，也不回成功。
                permits.CancelByInitiator(creator.Id);
                await Reject(context, CmdPacketType.REQUEST_GUILD_CREATE_PERMIT, PlayerMissing, "guild_permit_deliver_failed", creator.Id);
                return CommandResult.Handled;
            }
            await context.Reply(CmdPacketType.REQUEST_GUILD_CREATE_PERMIT, Available, "guild_permit_requested");
            log.Write("guild_permit", new { Result = "requested", Region = region, Initiator = creator.Id, Invitee = target.Id, Guild = draft.Name });
            return CommandResult.Handled;
        }

        // 被邀请方不在线（或账号在线但当前不是这个角色）：申请留在登记表里等它入城。
        await context.Reply(CmdPacketType.REQUEST_GUILD_CREATE_PERMIT, Available, "guild_permit_requested_pending");
        log.Write("guild_permit", new { Result = "requested_pending", Region = region, Initiator = creator.Id, Invitee = target.Id,
            Guild = draft.Name, InviteeOnline = invitee is not null, LifetimeSeconds = PermitLifetimeSeconds });
        return CommandResult.Handled;
    }

    /// <summary>
    /// 被邀请方答复（CMD 0x2E6，包体恰好 1 字节）。接受时按创建者是否在线分两条提交路径：
    ///
    /// - **创建者在线且正是发起角色**：经 <see cref="OnlineSessionDirectory"/> 在**创建者自己的会话**里提交，
    ///   认可方与创建者都安装并发布已提交的公会，结果在各自连接上发送。
    /// - **创建者离线**（顺序模式：创建者已经下线，被邀请方之后才入城认可）：用离线账号租约提交
    ///   （<see cref="GuildDirectory.CreateOffline"/>），创建者下次上线选角时读到的就是已扣费、已入会的最新快照。
    ///   账号仍被在线连接持有时不抢线，本次不建会、不动任何人的钱。
    ///
    /// 失败一律如实回被邀请方，且**不建会、不扣钱**：创建者不在发起角色 → 3（「无法找到指定玩家」）；
    /// 创建者离线提交失败（金币不足/名称被占用等）→ 103（表内语义最近的「公会邀请的等待时间已达到上限」，
    /// 具体原因留在服务端日志）。被邀请方的答复同时接受「接受」，是唯一会写库的分支。
    /// </summary>
    async Task<CommandResult> ReplyPermit(GameConnectionContext context, Packet request)
    {
        bool accepted = GuildRequests.PermitReply(request.Body);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var invitee = context.Session.Ready && context.Session.Phase == SessionPhase.Town ? context.Session.Selected : null;
        var permit = invitee is null ? null : permits.Take(invitee.Id, now);
        if (permit is null || invitee is null)
        {
            await Reject(context, CmdPacketType.REPLY_GUILD_CREATE_PERMIT, PermitMissing, "guild_permit_missing", 0);
            return CommandResult.Handled;
        }
        var creator = online.Find(permit.InitiatorAccount);
        if (!accepted)
        {
            // 拒绝（含客户端 60 秒超时自动发的 0）：不建会、不扣钱，只把结果告诉创建者。
            if (creator is not null)
                await Publish(creator, GuildPermitOutcome.Rejected(permit.InitiatorCharacterId));
            await Reject(context, CmdPacketType.REPLY_GUILD_CREATE_PERMIT, CreateFailed, "guild_permit_rejected", permit.InitiatorCharacterId);
            return CommandResult.Handled;
        }
        if (directory.InGuild(invitee.Id))
        {
            if (creator is not null)
                await Publish(creator, GuildPermitOutcome.Rejected(permit.InitiatorCharacterId, GuildCreateError.AlreadyInGuild));
            await Reject(context, CmdPacketType.REPLY_GUILD_CREATE_PERMIT, ReplyAlreadyInGuild, "guild_permit_invitee_in_guild",
                permit.InitiatorCharacterId);
            return CommandResult.Handled;
        }
        var outcome = creator?.ResolveGuildPermit(directory, permit, now);
        bool offline = false;
        if (outcome is null)
        {
            // 创建者不在线（或在线但不是发起角色/未入城）：顺序模式用离线账号租约提交；租约拿不到就如实失败。
            outcome = directory.CreateOffline(permit, owners, now);
            offline = outcome is not null;
        }
        if (outcome is null)
        {
            // 创建者账号仍被在线连接持有：不抢线、不建会、不动任何人的钱，被邀请方按「找不到指定玩家」答复。
            await Reject(context, CmdPacketType.REPLY_GUILD_CREATE_PERMIT, PlayerMissing, "guild_permit_creator_online",
                permit.InitiatorCharacterId);
            return CommandResult.Handled;
        }
        // 先答被邀请方（自己连接的写队列），再把结果发到创建者连接：创建者的慢连接不拖住这一侧。
        // 在线创建者的提交失败沿用表外的 34（被邀请方无文案可弹）；离线提交失败用 103 如实报「无法完成」。
        await context.Reply(CmdPacketType.REPLY_GUILD_CREATE_PERMIT,
            outcome.Flag == 1 ? Available : Error(offline ? PermitMissing : CreateFailed), "guild_permit_reply");
        if (outcome.Flag == 1 && directory.Membership(invitee.Id) is { } membership &&
            context.Session.InstallGuildMembership(invitee.Id, membership) is { } receipt)
            await projection.JoinedAsync(context, receipt);
        if (creator is not null)
            await Publish(creator, outcome);
        Log(outcome.Flag == 1 ? "created" : "failed", permit.InitiatorCharacterId, permit.InviteeCharacterId, permit.Name, outcome.Error,
            offline ? "offline_lease" : "creator_session");
        return CommandResult.Handled;
    }

    /// <summary>
    /// 创建者**关闭创建窗口**（CMD 0x2E7，空包体）。客户端没有这条 opcode 的处理器，因此**不回包**。
    ///
    /// 语义（实机确证）：`0x2E7` 是「创建窗口关掉了」，**不是取消认证邀请**。顺序模式下用户的操作就是
    /// 「邀请完关掉客户端 → 换账号上线认可」，早期实现据此回收申请，于是被邀请方上线永远收不到弹窗
    /// （`guild_permit` 日志当时是 `cancelled`）。这里因此只清创建窗口草稿：登记表里的待认证申请继续有效
    /// （<see cref="PermitLifetimeSeconds"/> 秒），被邀请方入城仍由 <see cref="GuildPermitDelivery"/> 补投
    /// `NOTI 0x2AD`，接受即扣费建会。也**不再推 `NOTI 0x2AF`**：那个包会让被邀请方关窗并自动回 `0x2E6[00]`，
    /// 等于换一种方式取消申请。
    ///
    /// 取消能力保留在两处：申请自然过期（600 秒，见 <see cref="GuildPermitRegistry.Find"/> /
    /// <see cref="GuildPermitRegistry.Take"/>），以及将来的正式取消协议
    /// （<see cref="GuildPermitRegistry.CancelByInitiator"/> 与 `NOTI 0x2AF` 的投递路径都在，
    /// 当前只有 `0x2E5` 当场投递失败的回滚会用到前者）。
    /// </summary>
    Task<CommandResult> CloseCreateWindow(GameConnectionContext context, Packet request)
    {
        if (context.Session.Selected is { } creator)
        {
            permits.ClearDraft(creator.Id);
            log.Write("guild_permit", new { Result = "window_closed_draft_cleared", Initiator = creator.Id });
        }
        return Task.FromResult(CommandResult.Handled);
    }

    /// <summary>把结果交给创建者连接自己的投影；创建者连接这时断掉也不能让被邀请方的答复变成失败。</summary>
    async Task Publish(GameSession creator, GuildPermitOutcome outcome)
    {
        try
        {
            await creator.PublishGuildPermitAsync(outcome);
        }
        catch (Exception failure) when (failure is IOException or OperationCanceledException or InvalidOperationException)
        {
            log.Write("guild_permit", new { Result = "publish_failed", Initiator = outcome.InitiatorCharacterId, Error = failure.Message });
        }
    }

    static async Task<CommandResult> Reply(GameConnectionContext context, Packet request, CmdPacketType opcode, string action,
        ushort lengthCode, ushort takenCode, ushort illegalCode, Func<string, GuildTextStatus> check, Action<ushort, string, long> remember)
    {
        byte[] body;
        try
        {
            string text = GuildRequests.Text(request.Body);
            var status = check(text);
            // 只有「可用」的文本才是创建窗口里的最终值：客户端只在查重通过后解锁创建与邀请。
            if (status == GuildTextStatus.Available)
                Remember(context, text, remember);
            body = status switch
            {
                GuildTextStatus.Available => Available,
                GuildTextStatus.Taken => Error(takenCode),
                GuildTextStatus.Illegal => Error(illegalCode),
                _ => Error(lengthCode),
            };
        }
        catch (InvalidDataException)
        {
            // 正文宽度错误按长度不合规回复，不伪造「可用」。
            body = Error(lengthCode);
        }
        await context.Reply(opcode, body, action);
        return CommandResult.Handled;
    }

    static void Remember(GameConnectionContext context, string text, Action<ushort, string, long> remember)
    {
        if (context.Session.Selected is { } creator)
            remember(creator.Id, text, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    async Task Reject(GameConnectionContext context, CmdPacketType opcode, ushort code, string action, ushort initiator)
    {
        await context.Reply(opcode, Error(code), action);
        log.Write("guild_permit", new { Result = action, Initiator = initiator, Code = code });
    }

    void Log(string result, ushort initiator, ushort invitee, string name, GuildCreateError error = GuildCreateError.None,
        string? mode = null) =>
        log.Write("guild_permit", new { Result = result, Mode = mode, Initiator = initiator, Invitee = invitee, Guild = name,
            Error = error == GuildCreateError.None ? null : error.ToString() });

    static byte[] Error(ushort code) => [0x00, (byte)(code & 0xFF), (byte)(code >> 8)];
}
