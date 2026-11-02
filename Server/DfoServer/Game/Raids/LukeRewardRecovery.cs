using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Raids;

// 管理入口必须显式给出历史身份，不能从当前Battle或最近一次记录推断。
internal sealed record LukeRewardRecoveryTarget(ushort Character, Guid Instance, int Phase, byte Kind, byte Card)
{
    public void Validate()
    {
        if (Instance == Guid.Empty || Phase is not (0 or 1))
            throw new InvalidDataException("历史攻坚身份无效");
    }
}
internal sealed record LukeRewardRecoverySnapshot(PreparedRaidReward Prepared, Guid Event, long Anchor,
    bool Claimed, ImmutableArray<uint> MailIds);
// 分页按原实例主键推进；空页也可能有续页，不从时间或最新记录推断身份。
internal sealed record LukeRewardRecoveryEntry(RaidPhaseEligibility Eligibility,
    ImmutableArray<LukeRewardRecoverySnapshot> Claimed)
{
    // 同kind只能领取一张；已领取的其它备选牌不是剩余权益。
    public ImmutableArray<RaidRewardOffer> AvailableOffers => Eligibility.Offers
        .Where(offer => !Claimed.Any(receipt => receipt.Prepared.Kind == offer.Kind)).ToImmutableArray();
}
internal sealed record LukeRewardRecoveryPage(ImmutableArray<LukeRewardRecoveryEntry> Entries, Guid? NextAfter);
internal interface ILukeRewardRecoveryStore : IRaidRewardStore
{
    LukeRewardRecoveryPage ListLukeRewardRecovery(AccountOwners.Lease owner, Character character, Guid? afterInstance = null, int limit = 16);
    LukeRewardRecoverySnapshot ReadLukeRewardRecovery(AccountOwners.Lease owner, Character character, LukeRewardRecoveryTarget target);
}
internal static class LukeRewardRecovery
{
    public static RaidRewardCommit Claim(AccountOwners.Lease owner, Character character, LukeRewardRecoveryTarget target,
        Guid expectedEvent, long expectedAnchor, ILukeRewardRecoveryStore store,
        Func<(ItemCatalog Items, GoldLimitCatalog Gold, EpicPieceCatalog Pieces)> resources, DateTimeOffset now) => owner.Execute(() =>
    {
        target.Validate();
        var saved = store.ReadLukeRewardRecovery(owner, character, target);
        if (expectedEvent == Guid.Empty || saved.Event != expectedEvent || saved.Anchor != expectedAnchor)
            throw new InvalidDataException("历史阶段资格已变化，请重新核对明确目标");
        // 已成功的原请求先重放，不因模板变化、背包容量或资源未装配而再次规划资产。
        if (saved.Claimed) return new RaidRewardCommit(character, true, saved.MailIds);
        var catalog = resources();
        var granted = InventoryRewardGrantService.TryGrantBatch(character, saved.Prepared.Grants,
            catalog.Items, catalog.Gold, catalog.Pieces, utcNow: now, allowOverflowMail: true, raidReward: true);
        if (!granted.Accepted || granted.Plan is not { } plan)
            throw new InvalidDataException("原奖励当前无法合法规划，未作替换或补发");
        var inventory = new InventoryPlan(plan.Before, plan.After, "raid_reward", new(character), plan.OverflowItems)
            { AccountMaterialRaidWeek = plan.AccountMaterialRaidWeek, AccountMaterialRaidDefinitions = plan.AccountMaterialRaidDefinitions };
        return store.CommitRaidReward(owner, character, saved.Prepared, inventory, now)
            ?? throw new InvalidDataException("原阶段资格未通过提交复验");
    });
}
