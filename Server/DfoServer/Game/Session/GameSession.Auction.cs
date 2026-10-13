using DfoServer.Game.Auction;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    /// <summary>
    /// 上架提交（CMD 183 的第 ② ③ 步）。与邮箱寄送同一形态（见 <see cref="GameSession.Mailbox"/> 的
    /// <c>InstallMailResult</c>）：**本方法是上架路径唯一能安装在线快照的地方**。
    ///
    /// 为什么必须在 Session 里做（而不是留在 Handler 里调 <see cref="AuctionService.Register"/>）：
    ///   ① `Selected` 只能由 Session 替换（它持有选角 epoch 与 owner generation），
    ///      而「背包扣减 + 写挂牌」是一次**真实提交**——提交后的角色快照（新 revision、源槽已空）
    ///      必须安装回会话，否则在线快照会停在提交前：下一笔背包操作（含**连续上架第二件**）会在
    ///      写事务里被 revision 复核判为陈旧快照，抛异常并断开连接。
    ///   ② 背包投影只认 **已经安装的快照** 产出的 <see cref="InventoryCommitReceipt"/>（见
    ///      <see cref="GameSession.CanPublishInventory"/> 与 <c>InventoryProjection.RefreshAsync</c>）。
    ///      上架不走常规背包写用例，因此这里复用**同一种**回执类型产出回执，而不是让整列刷新走一条
    ///      不需要回执的旁路——旁路会绕过 generation/epoch/revision 校验，把一个未经验证的快照发出去。
    ///
    /// 事务边界不变：`service.Register` 内部把背包扣减、挂牌插入与背包审计放进**同一个外层事务**，
    /// 失败整体回滚并向上抛（不返回成功）。本方法只在它成功返回后才替换在线快照。
    ///
    /// 返回的 <c>Inventory</c> 是本次上架的整列刷新请求（<see cref="InventoryResult.WholeListRefresh"/>），
    /// 仅在成功时非 null：它的 <see cref="InventoryResult.Receipt"/> 与刚安装的快照同源，
    /// 投影因此拿到「清空源槽 + 整列重发」所需的全部依据。Handler 只负责把它交给投影。
    /// </summary>
    public (AuctionListingRegisterResult Listing, InventoryResult? Inventory) RegisterListing(
        AuctionService service, Character expected, AuctionRegisterPlan plan) => owner.Execute(() =>
    {
        // 规划在锁外做，因此进锁后先复核「计划所依据的角色就是当前在线角色」：
        // 换角/接管/快照被别的提交替换后不能再拿旧计划去扣道具（仓储还会在写事务内再核一次 revision）。
        if (!Ready || Phase != SessionPhase.Town || !ReferenceEquals(Selected, expected))
            return (AuctionListingRegisterResult.Reject(AuctionRegisterError.StalePlan), (InventoryResult?)null);
        var previous = expected.ActiveMainQuests;
        var result = service.Register(owner, expected, plan);
        if (result.Character is null)
            return (result, null);
        Selected = result.Character;
        var receipt = new InventoryCommitReceipt(Selected, owner.Generation, SelectionEpoch)
        {
            Phase = Phase,
            PreviousQuests = previous
        };
        var change = new InventoryResult(true, 0, plan.Inventory) { WholeListRefresh = true, Receipt = receipt };
        return (result, change);
    });

    /// <summary>
    /// 取消上架提交（CMD 184）。与 <see cref="RegisterListing"/> **同一形态、同一份回执类型**：
    /// 提交成功后安装提交后的角色快照，并产出背包投影能发布的库存结果。
    ///
    /// 与上架的两点差别：
    ///   ① 方向相反：道具**回到背包**（新增/并入槽位），因此这里发**常规差量**即可 ——
    ///      客户端没有「源槽被业务窗口持有」的残留状态，不需要整列重建；
    ///   ② 因此这条路径同样不能依赖「只有常规背包写用例才有的回执」：回执由会话在安装快照时产出，
    ///      提交后的刷新**永远不是失败点**（上一轮断线事故的教训）。
    ///
    /// 事务边界：`service.Cancel` 内部把「道具还回背包 + 挂牌置为已下架 + 背包审计」放进**同一个外层事务**，
    /// 任一步失败整体回滚并向上抛（不返回成功），本方法只在成功返回后才替换在线快照。
    /// </summary>
    public (AuctionListingCancelResult Listing, InventoryResult? Inventory) CancelListing(
        AuctionService service, Character expected, AuctionCancelPlan plan) => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Town || !ReferenceEquals(Selected, expected))
            return (AuctionListingCancelResult.Reject(AuctionCancelError.StalePlan), (InventoryResult?)null);
        var previous = expected.ActiveMainQuests;
        var result = service.Cancel(owner, expected, plan);
        if (result.Character is null)
            return (result, null);
        Selected = result.Character;
        var receipt = new InventoryCommitReceipt(Selected, owner.Generation, SelectionEpoch)
        {
            Phase = Phase,
            PreviousQuests = previous
        };
        var change = new InventoryResult(true, 0, plan.Inventory) { Receipt = receipt };
        return (result, change);
    });

    /// <summary>
    /// 一口价购买提交（CMD 185）。与 <see cref="RegisterListing"/> / <see cref="CancelListing"/> 同一形态：
    /// **本方法是购买路径唯一能安装在线快照的地方**，提交成功后替换 <see cref="Selected"/> 并铸造
    /// <see cref="InventoryCommitReceipt"/>（回执与刚安装的快照同源，任何后续发布都拿它做 generation /
    /// 选角 epoch / revision 校验）。
    ///
    /// ★ **不发布任何背包或金币刷新**，所以返回的 <c>Inventory</c> 恒为 null —— 这不是省略，而是必须：
    /// 客户端 185 回调会**本地乐观扣一次金币**，服务端再补发 `0x000D`/`0x000E` 会被叠加成「再扣一次」
    /// （参考实现实测：余额不足时界面显示「余额清空」）。买方道具与卖方金币都通过邮件模块的**领取路径**
    /// 入包/入钱包，那条路径有自己的提交回执与差量发布；服务端只把内存金币值同步成扣款后的值，
    /// 供重新选角等后续刷新读到正确数字。
    ///
    /// 事务边界：`service.Buy` 内部把「买方扣款 + 挂牌置为已成交 + 两封托管邮件」放进**同一个外层事务**，
    /// 失败整体回滚并向上抛（这里**不 catch、不吞异常**），本方法只在它成功返回后才替换在线快照。
    /// 条件更新 0 行（被别人买走 / 重复点击）不是异常出口：<see cref="AuctionService.Buy"/> 已把它翻成
    /// 领域拒绝（静默码 `0x7e`），此时 `Character` 为 null，快照保持原样。
    /// </summary>
    public (AuctionListingBuyResult Buy, InventoryCommitReceipt? Receipt) BuyListing(
        AuctionService service, Character expected, AuctionBuyPlan plan) =>
        owner.Execute<(AuctionListingBuyResult Buy, InventoryCommitReceipt? Receipt)>(() =>
    {
        // 规划在锁外做，因此进锁后先复核「计划所依据的角色就是当前在线角色」：
        // 换角/接管/快照被别的提交替换后不能再拿旧计划去扣钱（仓储还会在写事务内再核一次 revision）。
        if (!Ready || Phase != SessionPhase.Town || !ReferenceEquals(Selected, expected))
            return (AuctionListingBuyResult.Reject(AuctionBuyError.StalePlan, "规划资格已变化：换角、接管或阶段不符"), null);
        var result = service.Buy(owner, expected, plan);
        if (result.Character is null)
            return (result, null);
        Selected = result.Character;
        var receipt = new InventoryCommitReceipt(Selected, owner.Generation, SelectionEpoch)
        {
            Phase = Phase,
            PreviousQuests = expected.ActiveMainQuests
        };
        return (result, receipt);
    });

    /// <summary>
    /// 到期结算（**到期未售**：道具退回 + 保证金退还）。与上面三条路径的差别只有一点：
    /// **不改角色行**（钱与道具都进托管邮件），因此不安装新快照、也不产出背包回执 ——
    /// 卖家领取邮件时走邮件模块自己的提交与差量发布。
    ///
    /// 仍然在 owner 锁内提交（与同账号其它提交串行），并在进锁后复核「计划所依据的角色仍是当前在线角色」：
    /// 换角/接管之后不能再拿旧计划去结算上一个角色的挂牌。
    /// </summary>
    public AuctionExpiryResult SettleExpiredListings(AuctionService service, Character expected, AuctionExpiryPlan plan) =>
        owner.Execute(() =>
        {
            if (!Ready || Phase != SessionPhase.Town || !ReferenceEquals(Selected, expected))
                return AuctionExpiryResult.None;
            return service.SettleExpired(owner, expected, plan);
        });
}
