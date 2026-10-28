namespace DfoServer.Game.Guild;

/// <summary>
/// 待认证的建会申请（内存态，不落库）：创建者提交的公会参数 + 被邀请角色 + 大区序号 + 截止时间。
/// 顺序模式下创建者与被邀请方**不是同时在线**：被邀请方可能在申请登记之后才上线入城，
/// 因此申请由 <see cref="GuildPermitRegistry.Find"/> 在入城时补投，并以
/// <see cref="ExpiresAtUnix"/> 兜底清理（客户端自己的 60 秒超时拒绝仍走取出即删的 Take）。
/// </summary>
internal sealed record GuildPermit(
    byte Region,
    string Name,
    string Address,
    string Promote,
    ushort InitiatorCharacterId,
    string InitiatorAccount,
    ushort InviteeCharacterId,
    string InviteeAccount,
    long ExpiresAtUnix)
{
    public string InitiatorName { get; init; } = "";
}

/// <summary>
/// 创建窗口的文本草稿（内存态，不落库）。认证邀请 `CMD 0x2E5` **只带大区序号与目标角色名**，
/// 公会名/地址/介绍只出现在名称查重（`0x9C`）、地址查重（`0x9D`）与介绍校验（`0x2E3`）请求里，
/// 因此服务端按发起角色记住最近一次「可用」的文本，认证通过时据此建会。
/// 地址缺失时回退为公会名：本版客户端没有域名输入框（`0x9D` 无发包点），
/// 而 `guilds.address` 是 NOT NULL + 唯一，名称唯一即可保证回退值唯一。
/// </summary>
internal sealed record GuildCreateDraft(string Name, string Address, string Promote, long UpdatedAtUnix)
{
    /// <summary>建会用的地址：客户端报过域名就用它，否则用公会名兜底。</summary>
    public string AddressOrName => Address.Length > 0 ? Address : Name;
}

/// <summary>
/// 建会认证申请的登记表。一个被邀请角色同时只保留一条待认证申请（重复邀请覆盖旧条目），
/// 答复时取出即删；过期条目在取出或补投查询时判定为不存在，不返回半成品。
/// 同一张表按发起角色维护创建窗口草稿：草稿随窗口关闭（`0x2E7`）丢弃，而申请**不受关窗影响**，
/// 仍按自己的有效期等被邀请方上线认可。
/// </summary>
internal sealed class GuildPermitRegistry
{
    /// <summary>草稿存活时间：创建窗口可以停留，超过即视为另一次创建（不沿用上一次的公会名）。</summary>
    internal const long DraftLifetimeSeconds = 600;

    readonly Lock gate = new();
    readonly Dictionary<ushort, GuildPermit> byInvitee = [];
    readonly Dictionary<ushort, ushort> byInitiator = [];
    readonly Dictionary<ushort, GuildCreateDraft> drafts = [];

    public int Count { get { lock (gate) return byInvitee.Count; } }

    public int DraftCount { get { lock (gate) return drafts.Count; } }

    public bool HasParticipant(ushort characterId, long now)
    {
        lock (gate) return byInvitee.Values.Any(permit => permit.ExpiresAtUnix > now &&
            (permit.InitiatorCharacterId == characterId || permit.InviteeCharacterId == characterId));
    }

    /// <summary>名称查重成功后记入草稿（只有「可用」的文本才会成为建会名）。</summary>
    public void RememberName(ushort initiatorCharacterId, string name, long now) =>
        Remember(initiatorCharacterId, now, draft => draft with { Name = name });

    public void RememberAddress(ushort initiatorCharacterId, string address, long now) =>
        Remember(initiatorCharacterId, now, draft => draft with { Address = address });

    public void RememberPromote(ushort initiatorCharacterId, string promote, long now) =>
        Remember(initiatorCharacterId, now, draft => draft with { Promote = promote });

    /// <summary>取发起角色的创建窗口草稿；过期的草稿顺手清掉并按不存在返回。</summary>
    public GuildCreateDraft? Draft(ushort initiatorCharacterId, long now)
    {
        lock (gate)
        {
            if (!drafts.TryGetValue(initiatorCharacterId, out var draft))
                return null;
            if (Expired(draft, now))
            {
                drafts.Remove(initiatorCharacterId);
                return null;
            }
            return draft;
        }
    }

    /// <summary>
    /// 丢弃创建窗口草稿（`CMD 0x2E7` 关窗，或建会成功后），不让旧文本进入下一次创建。
    ///
    /// **只动草稿，不动 <see cref="byInvitee"/> 里的待认证申请**：`0x2E7` 是「窗口关掉了」而不是「取消邀请」，
    /// 顺序模式下创建者正是关掉客户端再换账号上线认可，申请必须继续有效到
    /// <see cref="GuildPermit.ExpiresAtUnix"/>（建会用的公会名/地址/介绍已经复制进 <see cref="GuildPermit"/>，
    /// 不依赖草稿）。
    /// </summary>
    public void ClearDraft(ushort initiatorCharacterId)
    {
        lock (gate)
            drafts.Remove(initiatorCharacterId);
    }

    /// <summary>写草稿的单一入口：三个字段各自更新，不覆盖其它字段；过期草稿从空开始。</summary>
    void Remember(ushort initiatorCharacterId, long now, Func<GuildCreateDraft, GuildCreateDraft> update)
    {
        lock (gate)
        {
            var draft = drafts.TryGetValue(initiatorCharacterId, out var existing) && !Expired(existing, now)
                ? existing : new GuildCreateDraft("", "", "", now);
            drafts[initiatorCharacterId] = update(draft) with { UpdatedAtUnix = now };
        }
    }

    static bool Expired(GuildCreateDraft draft, long now) => draft.UpdatedAtUnix + DraftLifetimeSeconds < now;

    /// <summary>登记一条申请：发起者索引同时更新，便于创建者取消时反查被邀请者。</summary>
    public void Register(GuildPermit permit)
    {
        lock (gate)
        {
            if (byInitiator.TryGetValue(permit.InitiatorCharacterId, out ushort previous) &&
                byInvitee.TryGetValue(previous, out var previousPermit) && previousPermit.InitiatorCharacterId == permit.InitiatorCharacterId)
                byInvitee.Remove(previous);
            // 目标被另一创建者重新邀请时，旧创建者不能再通过反向索引删除新申请。
            if (byInvitee.TryGetValue(permit.InviteeCharacterId, out var replaced))
                byInitiator.Remove(replaced.InitiatorCharacterId);
            byInvitee[permit.InviteeCharacterId] = permit;
            byInitiator[permit.InitiatorCharacterId] = permit.InviteeCharacterId;
        }
    }

    /// <summary>
    /// 按被邀请角色查**未过期**申请，**不移除**：顺序模式下被邀请方入城时据此补投 `NOTI 0x2AD`，
    /// 它之后仍要能用 `0x2E6 [00]` 拒绝，因此补投不能消耗申请（消耗只发生在答复里）。
    /// 顺手清掉过期条目：过期的申请不会再被投递，也不能被答复。
    /// </summary>
    public GuildPermit? Find(ushort inviteeCharacterId, long now)
    {
        lock (gate)
        {
            if (!byInvitee.TryGetValue(inviteeCharacterId, out var permit))
                return null;
            if (permit.ExpiresAtUnix < now)
            {
                byInvitee.Remove(inviteeCharacterId);
                byInitiator.Remove(permit.InitiatorCharacterId);
                return null;
            }
            return permit;
        }
    }

    /// <summary>被邀请角色答复（或超时后到达）时取出申请；已过期视为不存在。</summary>
    public GuildPermit? Take(ushort inviteeCharacterId, long now)
    {
        lock (gate)
        {
            if (!byInvitee.Remove(inviteeCharacterId, out var permit))
                return null;
            byInitiator.Remove(permit.InitiatorCharacterId);
            return permit.ExpiresAtUnix < now ? null : permit;
        }
    }

    /// <summary>
    /// 按发起角色移除并返回其待认证申请。当前只有两处会用到：`0x2E5` 当场投递失败时的回滚
    /// （没有真的投出去就不能留下「服务端认为已邀请」的申请），以及将来的**正式取消协议**。
    /// `CMD 0x2E7`（创建者关窗）**不走这里**——关窗不是取消，见 <see cref="ClearDraft"/>。
    /// </summary>
    public GuildPermit? CancelByInitiator(ushort initiatorCharacterId)
    {
        lock (gate)
        {
            if (!byInitiator.Remove(initiatorCharacterId, out ushort invitee))
                return null;
            if (!byInvitee.TryGetValue(invitee, out var permit) || permit.InitiatorCharacterId != initiatorCharacterId)
                return null;
            byInvitee.Remove(invitee);
            return permit;
        }
    }
}
