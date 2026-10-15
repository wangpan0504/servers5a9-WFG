using System.Text;
using System.Text.RegularExpressions;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Guilds;

namespace DfoServer.Game.Guild;

/// <summary>公会目录的存储合同：查重与建会都只依赖数据库唯一索引，不在内存里另建第二套去重。</summary>
internal interface IGuildDirectoryStore
{
    /// <summary>公会名是否已被占用（`guilds.name` 唯一索引）。</summary>
    bool NameTaken(string name);

    /// <summary>公会地址是否已被占用（`guilds.address` 唯一索引）。</summary>
    bool AddressTaken(string address);

    /// <summary>角色当前所属公会 id；未加入任何公会返回 null（`guild_members.character_id` 唯一）。</summary>
    long? GuildOf(ushort characterId);

    GuildMembership? GuildMembershipOf(ushort characterId);

    /// <summary>
    /// 按角色名解析**认证邀请**的目标（只读一行）：角色 id、所属账号与是否已在公会。
    /// 查不到返回 null（客户端失败码 3）；同账号在用例层判定（失败码 5）；已在公会判定为失败码 23。
    /// 名称列是 `COLLATE NOCASE` 唯一索引，因此与客户端输入的大小写无关（与邮件收件人同一口径）。
    /// </summary>
    GuildPermitTarget? FindPermitTarget(string name);

    /// <summary>
    /// 创建公会提交：**同一事务**里扣建会花费、写入公会行与成员行（会长，以及计划里的被邀请角色）。
    /// 名称、地址与「一个角色只能在一个公会」在事务内复核；事务由本方法拥有，任一步失败整体回滚。
    /// </summary>
    GuildCreateResult CreateGuild(AccountOwners.Lease owner, Character expected, GuildCreatePlan plan);

    /// <summary>
    /// 顺序模式的离线建会按角色 id 读**最新快照**（含 revision 与完整背包）：创建者不在线时没有会话快照可用。
    /// 读在调用方已取得的账号租约内执行；角色不存在（或不属于该账号）返回 null。
    /// </summary>
    Character? LoadCharacterById(AccountOwners.Lease owner, ushort characterId);
}

/// <summary>创建窗口两次查重的判定结果，与客户端错误码一一对应。</summary>
internal enum GuildTextStatus
{
    Available,
    Taken,
    Length,
    Illegal,
}

/// <summary>
/// 公会名称/地址的文本规则。长度以客户端提示为准（「公会名称长度上限 : 中文7字符或英文14字符」），
/// 因此按 CP936 字节数取 14 上限。
/// 非法字符目前只拒绝控制字符：客户端自己的非法字符规则（错误码 0x6C/0x70）更严格，
/// 在拿到实机证据前不擅自收窄名称与地址的可用字符集，避免把客户端允许的输入判成非法。
/// </summary>
internal static partial class GuildTextRules
{
    internal const int MaxBytes = 14;

    public static GuildTextStatus Check(string text)
    {
        if (text.Length == 0) return GuildTextStatus.Length;
        int bytes;
        try
        {
            bytes = CharacterName.Encoding.GetByteCount(text);
        }
        catch (EncoderFallbackException)
        {
            return GuildTextStatus.Illegal;
        }
        if (bytes > MaxBytes) return GuildTextStatus.Length;
        return Control().IsMatch(text) ? GuildTextStatus.Illegal : GuildTextStatus.Available;
    }

    [GeneratedRegex("[\\x00-\\x1f\\x7f]")]
    private static partial Regex Control();

    /// <summary>
    /// 创建窗口「介绍」文本（CMD 0x2E3 校验）：**空串合法**（窗口计数器从 0/80 起），
    /// 长度上限由客户端计数器约束（80），服务端只拒绝控制字符。
    /// 客户端对该应答只区分「成功」与「非法字符（错误码 0x6C）」，其它码静默，因此不另造长度错误码。
    /// </summary>
    public static GuildTextStatus CheckPromote(string text) =>
        Control().IsMatch(text) ? GuildTextStatus.Illegal : GuildTextStatus.Available;
}

/// <summary>
/// 公会目录用例：创建窗口查重、建会规划、在线提交与离线认证提交。
/// 客户端的应答契约（CMD 通道、同号 opcode、`01` 可用 / `00 + u16 错误码`）由 Handler 负责，
/// 本层只给语义结果。
/// </summary>
internal sealed class GuildDirectory(IGuildDirectoryStore store, GuildParameterCatalog parameters)
{
    /// <summary>建会花费（PVF `[guild create cost]`）。</summary>
    public int CreateCost => parameters.CreateCost;

    public GuildTextStatus CheckName(string name) => Check(name, store.NameTaken);

    public GuildTextStatus CheckAddress(string address) => Check(address, store.AddressTaken);

    /// <summary>按名字解析认证邀请的目标（只读）：null = 角色不存在。</summary>
    public GuildPermitTarget? FindPermitTarget(string name) => store.FindPermitTarget(name);

    /// <summary>角色是否已在某个公会。认证链路上用于 `0x2E5` 的失败码 23 与接受时的复核。</summary>
    public bool InGuild(ushort characterId) => store.GuildOf(characterId) is not null;

    public GuildMembership? Membership(ushort characterId) => store.GuildMembershipOf(characterId);

    /// <summary>
    /// 只读规划：文本规则、是否已在公会、金币是否够建会花费。不写库、不改任何资产。
    ///
    /// <paramref name="inviteeCharacterId"/> 是**随创建者一起入会的被邀请角色**（认可建会认证的一方）：
    /// 认证链路必须带上它，提交时它的成员行与会长行同事务写入；0 = 没有随行成员（仅仓储级用例）。
    /// </summary>
    public (GuildCreateError Error, GuildCreatePlan? Plan) PlanCreate(Character character, string name, string address, long now,
        ushort inviteeCharacterId = 0)
    {
        if (GuildTextRules.Check(name) != GuildTextStatus.Available || GuildTextRules.Check(address) != GuildTextStatus.Available)
            return (GuildCreateError.InvalidText, null);
        if (store.GuildOf(character.Id) is not null)
            return (GuildCreateError.AlreadyInGuild, null);
        InventorySnapshot before = character.Inventory;
        if (before.Gold < (uint)parameters.CreateCost)
            return (GuildCreateError.NotEnoughGold, null);
        InventorySnapshot after = before with { Gold = before.Gold - (uint)parameters.CreateCost };
        return (GuildCreateError.None, new GuildCreatePlan(name, address, parameters.CreateCost, now,
            new InventoryPlan(before, after, "guild_create", new(character)), inviteeCharacterId));
    }

    /// <summary>
    /// 提交创建公会：调用方先 <see cref="PlanCreate"/>，提交结果带回实际写入的公会 id。
    /// </summary>
    public GuildCreateResult Create(AccountOwners.Lease owner, Character expected, GuildCreatePlan plan) =>
        store.CreateGuild(owner, expected, plan);

    /// <summary>
    /// **创建者离线**时的建会提交（顺序模式：创建者先发起认证，被邀请方之后才上线认可）。
    ///
    /// 取离线账号租约（与离线 GM 同一合同：`Reject` 策略，**账号仍被在线连接持有时不抢线**），
    /// 在租约内读该角色的最新快照 → <see cref="PlanCreate"/> → <see cref="Create"/>：
    ///
    /// - 返回 null = 没拿到租约（创建者账号仍在线，或不允许接管）：不建会、不动任何人的钱。
    /// - 返回 Flag=0 = 拿到了租约但规划/提交失败（金币不足、名称被占用等）：事务整笔回滚，同样不建会、不扣钱。
    /// - 返回 Flag=1 = 建会成功（<see cref="GuildPermitOutcome.CreatedOffline"/>：没有会话/连接可发布）。
    ///
    /// 离线提交没有会话可安装快照：扣费后的 revision 由数据库承担，创建者下次上线选角时读到的就是最新快照。
    /// </summary>
    public GuildPermitOutcome? CreateOffline(GuildPermit permit, AccountOwners owners, long now)
    {
        if (!owners.TryAcquire(permit.InitiatorAccount, static () => { }, DuplicateLoginPolicy.Reject, out var lease, out _) || lease is null)
            return null;
        using (lease)
        {
            if (store.LoadCharacterById(lease, permit.InitiatorCharacterId) is not { } created)
                return GuildPermitOutcome.Rejected(permit.InitiatorCharacterId, GuildCreateError.StalePlan);
            var (error, plan) = PlanCreate(created, permit.Name, permit.Address, now, permit.InviteeCharacterId);
            if (plan is null)
                return GuildPermitOutcome.Rejected(permit.InitiatorCharacterId, error);
            try
            {
                var result = Create(lease, created, plan);
                return result.Error == GuildCreateError.None && result.Character is not null
                    ? GuildPermitOutcome.CreatedOffline(permit.InitiatorCharacterId, result.GuildId)
                    : GuildPermitOutcome.Rejected(permit.InitiatorCharacterId, result.Error);
            }
            catch (InvalidOperationException) when (!lease.IsCurrent)
            {
                // 提交期间创建者登录并接管了账号：本次不建会（事务已回滚），也不把异常抛给被邀请方的连接。
                return GuildPermitOutcome.Rejected(permit.InitiatorCharacterId, GuildCreateError.StalePlan);
            }
        }
    }

    static GuildTextStatus Check(string text, Func<string, bool> taken)
    {
        GuildTextStatus status = GuildTextRules.Check(text);
        return status != GuildTextStatus.Available ? status :
            taken(text) ? GuildTextStatus.Taken : GuildTextStatus.Available;
    }
}
