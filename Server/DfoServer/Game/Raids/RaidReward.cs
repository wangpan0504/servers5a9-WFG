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
// 601：KB-20261026T063752-4691836ed4，C只证秒制显示，D只证整数，E只证rank动画选择。
// 读宽不独立证明业务值；Anton在结算候选冻结阶段用时/死亡。
// F经F==0写任务管理器+39C，抑制[raid phase clear]；非零允许继续匹配，不表示已领奖。
// 原生601成功分支发布阶段任务事件；正式Anton仅在阶段资格与任务同事务提交后允许该事件。
internal sealed record RaidResultDisplay(byte Result, byte Phase, uint Seconds, ushort RawValue, byte Rank, byte RawFlag);
internal sealed record RaidRewardOffer(byte Kind, byte Card, ImmutableArray<InventoryReward> Grants,
    ImmutableArray<RaidRewardDisplayRow> Display)
{
    // 历史JSON缺属性保持null；Anton仍仅使用原8B Display。
    public LukeRewardResultSpec? LukeResult { get; init; }
    public ImmutableArray<LukeRewardResultSpec> LukeAdditionalResults { get; init; } = [];
    public ImmutableArray<LukeRewardResultSpec> PreyDisplay { get; init; } = [];
}
internal sealed record RaidPhaseRewardPolicy(string Source, ImmutableArray<RaidRewardOffer> Offers, RaidResultDisplay Result)
{
    // 策略可显式禁用结果；正式Anton在资格提交后绑定真实阶段统计。
    public bool PublishResult { get; init; } = true;
}
internal sealed record PreparedRaidReward(Guid Instance, int Phase, byte Kind, byte Card, ushort Character,
    string Source, ImmutableArray<InventoryReward> Grants, bool FinalPhase, int Week)
{
    public RaidKind RaidType { get; init; } = RaidKind.Anton;
}
internal sealed record RaidRewardCommit(Character Character, bool Duplicate, ImmutableArray<uint> MailIds)
{
    // 事务内按数据库余额再裁后的实际发放。缺省表示与冻结 Grants 相同。
    public ImmutableArray<InventoryReward> SettledGrants { get; init; }
}
internal sealed record RaidRewardReceipt(PreparedRaidReward Prepared, RaidRewardOffer Offer, RaidRewardCommit Commit,
    InventoryResult Inventory, long Generation, long Epoch);

internal interface IRaidRewardStore
{
    RaidRewardCommit? CommitRaidReward(AccountOwners.Lease owner, Character expected, PreparedRaidReward prepared,
        InventoryPlan inventory, DateTimeOffset now);
}

// 每阶段资格独立冻结；不是资产计划，也不以领取次数代替最终通关次数。
internal sealed record RaidPhaseEligibility(Guid Instance, RaidKind RaidType, int Phase, Guid Event, long Anchor,
    ushort Character, bool CountsAsClear, bool Eligible, string Reason, string Source, int Week, ImmutableArray<RaidRewardOffer> Offers)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AntonPhaseQuestProgress? AntonQuestProgress { get; init; }
}
