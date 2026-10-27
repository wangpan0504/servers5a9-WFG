using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Raids;

internal static class RaidRewardRules
{
    // 产品规格hypothesis：终章安徒恩每角色每周2次，周期沿既有周四06:00业务周。
    public const int WeeklyClearLimit = 2;
}

// 展示列只保存已证读宽，不以UI显示数量替代实际发放计划。所有列均由明确配置提供，无推测默认值。
internal sealed record RaidRewardDisplayItem(byte RawCode, uint ItemId, ushort DisplayValue, byte RawFlag);
internal sealed record RaidRewardDisplayRow(ushort RawOwner, byte Card, ImmutableArray<RaidRewardDisplayItem> Items);
internal sealed record RaidResultDisplay(byte Result, byte Phase, uint Seconds, ushort RawValue, byte Rank, byte RawFlag);
internal sealed record RaidRewardOffer(byte Kind, byte Card, ImmutableArray<InventoryReward> Grants,
    ImmutableArray<RaidRewardDisplayRow> Display)
{
    // 历史JSON缺属性保持null；Anton仍仅使用原8B Display。
    public LukeRewardResultSpec? LukeResult { get; init; }
}
internal sealed record RaidPhaseRewardPolicy(string Source, ImmutableArray<RaidRewardOffer> Offers, RaidResultDisplay Result);
internal sealed record PreparedRaidReward(Guid Instance, int Phase, byte Kind, byte Card, ushort Character,
    string Source, ImmutableArray<InventoryReward> Grants, bool FinalPhase, int Week)
{
    public RaidKind RaidType { get; init; } = RaidKind.Anton;
}
internal sealed record RaidRewardCommit(Character Character, bool Duplicate, ImmutableArray<uint> MailIds);
internal sealed record RaidRewardReceipt(PreparedRaidReward Prepared, RaidRewardOffer Offer, RaidRewardCommit Commit,
    InventoryResult Inventory, long Generation, long Epoch);

internal interface IRaidRewardStore
{
    RaidRewardCommit? CommitRaidReward(AccountOwners.Lease owner, Character expected, PreparedRaidReward prepared,
        InventoryPlan inventory, DateTimeOffset now);
}

// 每阶段资格独立冻结；不是资产计划，也不以领取次数代替最终通关次数。
internal sealed record RaidPhaseEligibility(Guid Instance, RaidKind RaidType, int Phase, Guid Event, long Anchor,
    ushort Character, bool CountsAsClear, bool Eligible, string Reason, string Source, int Week, ImmutableArray<RaidRewardOffer> Offers);
