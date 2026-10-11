using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Characters;
using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

internal enum CardMembershipKind { BlackDiamond, GoldCard }

// 会员到期归账号；黄金牌次数按角色和中国时间06:00业务日记录，不接受客户端会员标志。
internal sealed record CardMembership(long BlackDiamondExpires = 0, long GoldCardExpires = 0,
    long Revision = 0, int Day = 0, int GoldCardUsed = 0)
{
    public const int GoldCardDailyLimit = 10;
    public bool BlackDiamondActive(long now) => BlackDiamondExpires > now;
    public bool GoldCardAvailable(long now) => GoldCardExpires > now && GoldCardUsed < GoldCardDailyLimit;
    public static int ServiceDay(DateTimeOffset now) => ShopPurchaseCount.Today(now);
}

internal sealed record DungeonCardOptions(DungeonCardReward Paid, int Price, bool UseGoldCard,
    CardMembership Membership, DungeonCardReward? BlackDiamond = null);

internal sealed record DungeonCardDelivery(InventoryPlan Inventory, ImmutableArray<DungeonCardReward> Rewards,
    ImmutableArray<ItemCore> MailItems, int Cost = 0);

internal sealed record DungeonPaidCardCommit(bool Accepted, Character? Character = null,
    DungeonCardReward? Reward = null, InventoryPlan? Inventory = null, uint? MailId = null,
    bool Duplicate = false);

internal sealed record FormalPaidCardResult(FormalDungeonReceipt Receipt, DungeonCardReward Reward,
    InventoryResult? Inventory, uint? MailId, bool Duplicate);

internal sealed record DungeonCardView(DungeonCardOptions? Options,bool FreeSelected,DungeonCardReward? Paid);
