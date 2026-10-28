using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public GuildPanelQuery? CaptureGuildPanelQuery() => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character) return null;
        long epoch = SelectionEpoch;
        return new GuildPanelQuery(character.Id, () => owner.IsCurrent && Ready &&
            SelectionEpoch == epoch && Selected?.Id == character.Id);
    });

    public GuildMembershipReceipt? InstallGuildMembership(ushort characterId, GuildMembership membership) => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Town || Selected is not { } current || current.Id != characterId)
            return null;
        Selected = current with { Guild = membership };
        return new GuildMembershipReceipt(Selected, owner.Generation, SelectionEpoch);
    });

    public GuildMembershipReceipt? CaptureGuildMembership(ushort characterId) => owner.Execute(() =>
        Ready && Phase == SessionPhase.Town && Selected is { Guild: not null } current && current.Id == characterId
            ? new GuildMembershipReceipt(current, owner.Generation, SelectionEpoch) : null);

    public bool CanPublishGuildMembership(GuildMembershipReceipt receipt) => owner.Execute(() =>
        owner.IsCurrent && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        Ready && Phase == SessionPhase.Town && ReferenceEquals(Selected, receipt.Character));

    /// <summary>
    /// 创建公会提交（与[拍卖行上架](GameSession.Auction.cs)同一形态）：**本方法是建会路径唯一能安装在线快照的地方**。
    ///
    /// 为什么必须在 Session 里做：建会要扣 `[guild create cost]` 金币，那是一次真实提交；提交后的角色快照
    /// （新 revision、余额已扣）必须安装回会话，否则在线快照停在提交前，**下一笔背包操作会在写事务里被
    /// revision 复核判为陈旧快照**并断线。背包投影也只认「已经安装的快照」产出的
    /// <see cref="InventoryCommitReceipt"/>，因此这里复用同一种回执类型，不另造旁路。
    ///
    /// 事务边界不变：`directory.Create` 内部把扣费、公会行与成员行（会长 + 计划里的被邀请角色）放进同一个外层事务，
    /// 失败整笔回滚并向上抛；本方法只在它成功返回后才替换在线快照。
    ///
    /// 返回的 <c>Inventory</c> 是**常规差量**（`WholeListRefresh = false`）：建会只改金币，没有
    /// 「源槽仍被业务窗口持有」的残留状态，不需要整列重建。
    /// </summary>
    public (GuildCreateResult Guild, InventoryResult? Inventory) CreateGuild(
        GuildDirectory directory, Character expected, GuildCreatePlan plan) => owner.Execute(() =>
    {
        // 规划在锁外做，因此进锁后先复核「计划所依据的角色就是当前在线角色」：
        // 换角/接管/快照被别的提交替换后不能拿旧计划去扣钱（仓储还会在写事务内再核一次 revision）。
        if (!Ready || Phase != SessionPhase.Town || !ReferenceEquals(Selected, expected))
            return (new GuildCreateResult(GuildCreateError.StalePlan, null, 0), (InventoryResult?)null);
        var previous = expected.ActiveMainQuests;
        var result = directory.Create(owner, expected, plan);
        if (result.Character is null)
            return (result, null);
        Selected = result.Character;
        var receipt = new InventoryCommitReceipt(Selected, owner.Generation, SelectionEpoch)
        {
            Phase = Phase,
            PreviousQuests = previous,
            PreviousSkillSwitchInventory = expected.SkillSwitchInventory
        };
        var change = new InventoryResult(true, 0, plan.Inventory) { Receipt = receipt };
        return (result, change);
    });

    /// <summary>
    /// 接受建会认证（被邀请方点了「接受」）时在**创建者自己的会话**里跑完整提交：
    /// 规划 → 提交 → 安装提交后的快照，一路都用创建者会话的 owner 锁与 generation
    /// ——跨账号租约与旧 generation 都不许写库，因此这一步只能落在创建者的会话上。
    ///
    /// 返回 null = 创建者会话当前不是发起角色（未入城 / 换角 / 已被接管）：**不建会、不动任何人的钱**；
    /// 创建者**离线**时的提交走 <see cref="GuildDirectory.CreateOffline"/>（顺序模式，离线账号租约）。
    /// 失败返回 Flag=0 的结果（推 `NOTI 0x2AE[0]`）；成功带回公会 id 与带提交回执的金币差额，
    /// 由创建者连接的背包投影负责发布。
    ///
    /// 被邀请角色由 <paramref name="permit"/> 带进计划：它认可认证即入会，成员行与会长行在同一事务里写入。
    ///
    /// 本方法只做提交，不发包：发包必须在锁外、且由目标连接自己的上下文完成。
    /// </summary>
    public GuildPermitOutcome? ResolveGuildPermit(GuildDirectory directory, GuildPermit permit, long now) => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Town || Selected is not { } creator || creator.Id != permit.InitiatorCharacterId)
            return (GuildPermitOutcome?)null;
        var (error, plan) = directory.PlanCreate(creator, permit.Name, permit.Address, now, permit.InviteeCharacterId);
        if (plan is null)
            return GuildPermitOutcome.Rejected(creator.Id, error);
        var (guild, inventory) = CreateGuild(directory, creator, plan);
        return guild.Error == GuildCreateError.None && inventory is not null
            ? GuildPermitOutcome.Created(creator.Id, guild.GuildId, inventory)
            : GuildPermitOutcome.Rejected(creator.Id, guild.Error);
    });

    /// <summary>被邀请方连接的受理状态：只有「正是当前在线角色且在城镇」才推认证窗口。</summary>
    public GuildInviteeState InviteeState(ushort characterId) => owner.Execute(() =>
        Selected?.Id != characterId ? GuildInviteeState.Absent :
        Ready && Phase == SessionPhase.Town ? GuildInviteeState.Ready : GuildInviteeState.Busy);

    // Runtime 绑定原连接的有界通知入口（与邮件 MailboxArrived 同一形态）；目录仍只保存这一份 Session。
    public Action<GuildPermitNotice>? GuildPermitNoticeArrived { private get; set; }

    /// <summary>
    /// 向被邀请角色所在连接推 `NOTI 0x2AD`（邀请）或 `NOTI 0x2AF`（创建者取消）。
    /// 投递前在同一把锁里复核它仍是目标角色：否则如实返回 false，不伪造「已投递」。
    /// </summary>
    public bool TryDeliverGuildPermit(ushort characterId, GuildPermitNotice notice)
    {
        try
        {
            var delivery = owner.Execute(() => Ready && Phase == SessionPhase.Town && Selected?.Id == characterId
                ? GuildPermitNoticeArrived : null);
            if (delivery is null)
                return false;
            delivery(notice);
            return true;
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return false; } // 刚被接管/替换的原连接不接收
    }

    // Runtime 绑定原连接的建会结果发布入口（金币差额 + NOTI 0x2AE/0x47/0x3A）。
    public Func<GuildPermitOutcome, Task>? GuildPermitResolved { private get; set; }

    /// <summary>把建会结果交给创建者连接自己的投影发布；锁内只取回调，发包在锁外。</summary>
    public async Task PublishGuildPermitAsync(GuildPermitOutcome outcome)
    {
        Func<GuildPermitOutcome, Task>? publish;
        try
        {
            publish = owner.Execute(() => GuildPermitResolved);
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return; }
        if (publish is not null)
            await publish(outcome);
    }
}
