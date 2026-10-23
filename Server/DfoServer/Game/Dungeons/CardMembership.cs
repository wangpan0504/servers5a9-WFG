using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Characters;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace DfoServer.Game.Dungeons;

internal enum CardMembershipKind { BlackDiamond, GoldCard }

// 会员到期归账号；黄金牌次数按角色和中国时间06:00业务日记录，不接受客户端会员标志。
internal sealed record CardMembership(long BlackDiamondExpires = 0, long GoldCardExpires = 0,
    long Revision = 0, int Day = 0, int GoldCardUsed = 0)
{
    public const int BlackDiamondType = 56;
    public const int DevilContractType = 58;
    public ImmutableArray<long> DevilServiceExpiries { get; init; } = [];
    public ImmutableArray<byte> DevilServiceUsed { get; init; } = [];
    // 仅兼容旧本地冻结回执；不装载或产生回归玩家资格。
    [JsonPropertyName("ReturningPlayerExpires")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long LegacyReturningPlayerExpires { get; init; }
    // 临时接入58的免费黄金牌子服务0（026E4A30(0)）；双倍规则及其他子服务独立。
    public static readonly bool DevilContractService0Enabled = true;
    public const int GoldCardDailyLimit = 10;
    public bool BlackDiamondActive(long now) => BlackDiamondExpires > now;
    public bool GoldCardActive(long now) => GoldCardExpires > now;
    public bool GoldCardAvailable(long now) => DevilContractService0Enabled && GoldCardActive(now) && GoldCardUsed < GoldCardDailyLimit;
    public static int ServiceDay(DateTimeOffset now) => ShopPurchaseCount.Today(now);
}

internal sealed record CardMembershipReceipt(Character Character, CardMembership Membership,
    long Generation, long SelectionEpoch, long Publication);

internal sealed record DungeonCardOptions(DungeonCardReward Paid, int Price, bool UseGoldCard,
    CardMembership Membership, DungeonCardReward? BlackDiamond = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PaidDisabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DoublePaidItems { get; init; }
    // 保留旧JSON形状供唯一回执核对，不投影成客户端回归标志。
    [JsonPropertyName("ReturningPlayer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacyReturningPlayer { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long GoldCardEventRevision { get; init; }
}

internal sealed record DungeonCardDelivery(InventoryPlan Inventory, ImmutableArray<DungeonCardReward> Rewards,
    ImmutableArray<ItemCore> MailItems, int Cost = 0);

internal sealed record DungeonPaidCardCommit(bool Accepted, Character? Character = null,
    DungeonCardReward? Reward = null, InventoryPlan? Inventory = null, uint? MailId = null,
    bool Duplicate = false);

internal sealed record FormalPaidCardResult(FormalDungeonReceipt Receipt, DungeonCardReward Reward,
    InventoryResult? Inventory, uint? MailId, bool Duplicate);

internal sealed record DungeonFreeCardCommit(Character Character, DungeonCardReward Reward,
    DungeonCardReward? BlackDiamond, InventoryPlan Inventory, uint? MailId, bool Duplicate = false);

internal sealed record FormalFreeCardResult(FormalDungeonReceipt Receipt, InventoryResult? Inventory,
    uint? MailId, bool Duplicate);

internal sealed record DungeonCardClaimPreparation(Character Expected, byte Side, DungeonCardDelivery Delivery,
    DateTimeOffset Time);

internal sealed record DungeonCardView(DungeonCardOptions? Options,bool FreeSelected,DungeonCardReward? Paid);
