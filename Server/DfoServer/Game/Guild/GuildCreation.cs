using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Guild;

/// <summary>
/// 创建公会的失败原因。Handler 接入时映射成客户端的 u16 错误码
/// （已解码的失败码：31=金币不足、32=已加入或等待加入；其余沿用通用失败码）。
/// </summary>
internal enum GuildCreateError
{
    None,
    InvalidText,
    NotEnoughGold,
    AlreadyInGuild,
    NameTaken,
    AddressTaken,

    /// <summary>规划所依据的角色已不是当前在线角色（换角/接管/快照被别的提交替换）：计划作废，不落库。</summary>
    StalePlan,
}

/// <summary>
/// 创建公会计划：名称、地址、建会花费（来自 PVF `[guild create cost]`）、提交后的背包快照与写入时间。
/// 提交时**扣费、公会行与成员行同事务**，任一步失败整体回滚：既有[拍卖行上架](../Auction/AuctionService.cs)的同一口径。
///
/// <see cref="InviteeCharacterId"/> 是**随创建者一起入会的被邀请角色**（认可建会认证的一方）：
/// 认证链路必须带上它，接受时会长成员行与它的成员行在同一事务里写入；0 表示没有随行成员。
/// </summary>
internal sealed record GuildCreatePlan(string Name, string Address, int Cost, long CreatedAtUnix, InventoryPlan Inventory,
    ushort InviteeCharacterId = 0);

/// <summary>
/// 创建公会提交结果。<see cref="Character"/> 是提交后的角色快照（与扣费同一事务），
/// <see cref="GuildId"/> 只在成功时非 0。
/// </summary>
internal sealed record GuildCreateResult(GuildCreateError Error, Character? Character, long GuildId);
