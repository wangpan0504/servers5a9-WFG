using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Guild;

/// <summary>
/// 认证邀请目标的只读解析结果（按角色名查一行）：角色 id、所属账号与是否已在公会。
/// 同账号、找不到、已在公会分别对应客户端 `0x2E5` 的失败码 5 / 3 / 23。
/// </summary>
internal sealed record GuildPermitTarget(ushort Id, string Account, bool InGuild);

/// <summary>
/// 被邀请方连接的受理状态：决定 `0x2E5` 是推邀请 `NOTI 0x2AD`，还是按客户端既有的
/// `NOTI 0x2AE[0x2D]`「对方正在挑战地下城」分支回复，或按失败码 3 拒绝。
/// </summary>
internal enum GuildInviteeState
{
    /// <summary>目标角色不在线或当前选的是别的角色，保留申请等待入城补投。</summary>
    Absent,

    /// <summary>在线但不在城镇（副本/加载/结算）：推 `NOTI 0x2AE[0x2D]`，不弹认证窗口。</summary>
    Busy,

    /// <summary>在城镇且入城完成：可以受理 `NOTI 0x2AD`。</summary>
    Ready,
}

/// <summary>发往**被邀请方**连接的通知种类：邀请（0x2AD）与创建者取消（0x2AF）。</summary>
internal enum GuildPermitNoticeKind : byte
{
    Invite,
    Cancelled,
}

/// <summary>
/// 发往被邀请方连接的通知，携带已冻结的邀请文本与目标身份。
/// </summary>
internal sealed record GuildPermitNotice(GuildPermitNoticeKind Kind, GuildPermit Permit);

/// <summary>
/// 建会认证的最终结果。<see cref="Flag"/> 原样作为 `NOTI 0x2AE` 的包体
/// （1=对方已认可、0=拒绝/建会失败、0x2D=对方在挑战地下城）；<see cref="GuildId"/> 与
/// <see cref="Inventory"/> 只在建会成功时有效，<see cref="Error"/> 只进服务端日志。
/// </summary>
internal sealed record GuildPermitOutcome(byte Flag, ushort InitiatorCharacterId, long GuildId, InventoryResult? Inventory,
    GuildCreateError Error = GuildCreateError.None)
{
    /// <summary>对方已认可且建会提交成功（金币差额随 <see cref="Inventory"/> 交给创建者连接的背包投影）。</summary>
    public static GuildPermitOutcome Created(ushort initiator, long guildId, InventoryResult inventory) =>
        new(1, initiator, guildId, inventory);

    /// <summary>
    /// 创建者**离线**时建会提交成功（顺序模式）：没有会话可以安装提交后的快照，也没有连接可以发布金币差额，
    /// 因此 <see cref="Inventory"/> 为 null；认可方仍在成功应答后安装并发布自己的入会状态。
    /// </summary>
    public static GuildPermitOutcome CreatedOffline(ushort initiator, long guildId) => new(1, initiator, guildId, null);

    /// <summary>拒绝、规划/提交失败或创建者已不在当前角色：只通知创建者，不动任何资产的余额。</summary>
    public static GuildPermitOutcome Rejected(ushort initiator, GuildCreateError error = GuildCreateError.None) =>
        new(0, initiator, 0, null, error);
}
