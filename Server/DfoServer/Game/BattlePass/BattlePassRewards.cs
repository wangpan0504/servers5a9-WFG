using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.BattlePass;

internal readonly record struct BattlePassRewardKey(byte Track, int Level);

// 账号归属由仓储和会话提供，不能从客户端请求取得。
internal sealed record BattlePassSeasonState(string SeasonKey, long TotalExperience, byte PassType,
    ImmutableHashSet<BattlePassRewardKey> Claims, long Revision = 0)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SeasonKey) || TotalExperience < 0 || Revision < 0 || Claims is null ||
            Claims.Any(key => key.Track > 1 || key.Level is < 1 or > 80))
            throw new InvalidDataException("Invalid battle-pass season state.");
    }

    public byte[] ToClaimBytes()
    {
        Validate();
        byte[] bytes = new byte[20];
        foreach (var key in Claims)
            bytes[10 * key.Track + (key.Level - 1) / 8] |= (byte)(1 << ((key.Level - 1) % 8));
        return bytes;
    }
}

internal sealed record BattlePassRewardPlan(InventoryPlan Inventory, BattlePassSeasonState Before,
    BattlePassSeasonState After, BattlePassRewardKey Reward)
{
    // 单领保留原构造合同；批领保存一次规划的完整、有序领取键。
    public ImmutableArray<BattlePassRewardKey> Rewards { get; init; } = [Reward];
}

internal sealed record BattlePassRewardResult(bool Accepted, ushort Error, BattlePassRewardPlan? Plan = null)
{
    public string? RejectReason { get; init; }
    public static BattlePassRewardResult Reject(ushort error = 19, string? reason = null) =>
        new(false, error) { RejectReason = reason };
}

internal sealed class BattlePassRewardService(BattlePassCatalog catalog, ItemCatalog items,
    GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, TimeProvider? clock = null, Func<DateTimeOffset, bool>? available = null)
{
    internal const string OverflowMailSender = "阿拉德探险记";
    internal const string OverflowMailText = "背包空间不足，奖励已通过邮件发放。";

    public BattlePassRewardResult Plan(Character character, BattlePassSeasonState state, byte action, byte track, uint key)
    {
        if (available?.Invoke((clock ?? TimeProvider.System).GetUtcNow()) == false)
            return BattlePassRewardResult.Reject(reason: "battle_pass_disabled");
        // 先验证无符号请求键，再窄化整数或计算领取位。
        if (action > 1 || action == 0 && track > 1 || key is < 1 or > 80)
            return BattlePassRewardResult.Reject(reason: "battle_pass_reward_target");
        try { state.Validate(); }
        catch (InvalidDataException) { return BattlePassRewardResult.Reject(reason: "battle_pass_state"); }

        if (action == 1) return PlanAll(character, state, track, key);

        int level = (int)key;
        var rewardKey = new BattlePassRewardKey(track, level);
        if (state.Claims.Contains(rewardKey) || track == 1 && state.PassType != 1 ||
            catalog.LevelAtTotalExperience((ulong)state.TotalExperience) < level ||
            !catalog.RewardsByLevel.TryGetValue(level, out var definition))
            return BattlePassRewardResult.Reject(reason: "battle_pass_reward_qualification");

        var rewards = definition.Rewards.Where(row => row.RawTrackCode == track)
            .Select(row => new InventoryReward(row.ItemId, row.Count)).ToArray();
        if (rewards.Length == 0)
            return BattlePassRewardResult.Reject(reason: "battle_pass_reward_empty");
        return Grant(character, state, [rewardKey], rewards);
    }

    BattlePassRewardResult PlanAll(Character character, BattlePassSeasonState state, byte passType, uint viewLevel)
    {
        int currentLevel = catalog.LevelAtTotalExperience((ulong)state.TotalExperience);
        // 2017一键按钮在页面创建时复制type和等级。豪华购买把资格改成1并把经验抬到40级后，
        // 按钮仍发送旧快照。旧值不能提高权威资格，也不能截断或拒绝已解锁的全集。
        if (passType > state.PassType || viewLevel > (uint)currentLevel)
            return BattlePassRewardResult.Reject(reason: "battle_pass_state_changed");
        var keys = ImmutableArray.CreateBuilder<BattlePassRewardKey>();
        var rewards = new List<InventoryReward>();
        for (int level = 1; level <= currentLevel; level++)
        {
            if (!catalog.RewardsByLevel.TryGetValue(level, out var definition)) continue;
            for (byte track = 0; track <= (state.PassType == 1 ? 1 : 0); track++)
            {
                var rewardKey = new BattlePassRewardKey(track, level);
                if (state.Claims.Contains(rewardKey)) continue;
                var rows = definition.Rewards.Where(row => row.RawTrackCode == track).ToArray();
                if (rows.Length == 0) continue;
                keys.Add(rewardKey);
                foreach (var row in rows) rewards.Add(new(row.ItemId, row.Count));
            }
        }
        if (keys.Count == 0)
            return BattlePassRewardResult.Reject(reason: "battle_pass_reward_empty");
        return Grant(character, state, keys.ToImmutable(), rewards);
    }

    BattlePassRewardResult Grant(Character character, BattlePassSeasonState state,
        ImmutableArray<BattlePassRewardKey> keys, IReadOnlyList<InventoryReward> rewards)
    {
        InventoryResult granted;
        try
        {
            // 规划前解析全部普通模板，后续奖励缺定义时整笔拒绝。
            foreach (var reward in rewards)
                if (!pieces.ByPiece.ContainsKey(reward.ItemId) && CurrencyWallet.RewardKind(reward.ItemId) is null &&
                    !items.TryGet(reward.ItemId, out _))
                    return BattlePassRewardResult.Reject(reason: "battle_pass_reward_definition");
            // 普通物品沿共享发放：能入包的入包，放不下的完整实例转溢出邮件。钱包/碎片仍须足额入账。
            granted = InventoryRewardGrantService.TryGrantBatch(character, rewards, items, goldLimits, pieces,
                utcNow: (clock ?? TimeProvider.System).GetUtcNow(), allowOverflowMail: true);
        }
        catch (InvalidDataException) { return BattlePassRewardResult.Reject(reason: "battle_pass_reward_definition"); }
        catch (OverflowException) { return BattlePassRewardResult.Reject(reason: "battle_pass_reward_overflow"); }

        if (!granted.Accepted || granted.Plan is not { } grant)
            return BattlePassRewardResult.Reject(granted.Error == 0 ? (ushort)19 : granted.Error, granted.RejectReason);
        if (!HasWholeBalances(character.Inventory, grant.After, rewards))
            return BattlePassRewardResult.Reject(17, "battle_pass_reward_balance_capacity");

        // 实例详情、期限和放置沿用共享发奖路径；修改审计原因时保留其全部元数据。
        var inventory = new InventoryPlan(grant.Before, grant.After, "battle_pass_reward",
            grant.CharacterPrecondition ?? new(character), grant.OverflowItems)
        {
            PreserveMaterials = grant.PreserveMaterials,
            UsedItems = grant.UsedItems,
            AccountRefillClaims = grant.AccountRefillClaims,
            DailyItemMails = grant.DailyItemMails,
            ResetItemIds = grant.ResetItemIds,
            ExpBonus = grant.ExpBonus,
            EquipmentSlotFlagsAfter = grant.EquipmentSlotFlagsAfter,
            UnlockSkillTree = grant.UnlockSkillTree,
            GuildHongbaoPurchase = grant.GuildHongbaoPurchase,
            RequiredEvent = grant.RequiredEvent,
            OverflowMailSender = BattlePassRewardService.OverflowMailSender,
            OverflowMailText = BattlePassRewardService.OverflowMailText
        };
        // 此处只是规划。仓储在完整发奖与唯一领取事实的同一事务中推进一次版本。
        var after = state with { Claims = state.Claims.Union(keys) };
        return new(true, 0, new(inventory, state, after, keys[0]) { Rewards = keys });
    }

    bool HasWholeBalances(InventorySnapshot before, InventorySnapshot after, IReadOnlyList<InventoryReward> rewards)
    {
        foreach (var group in rewards.Where(row => !pieces.ByPiece.ContainsKey(row.ItemId) && CurrencyWallet.RewardKind(row.ItemId) is not null)
            .GroupBy(row => CurrencyWallet.RewardKind(row.ItemId)!.Value))
            if (CurrencyWallet.Get(after, group.Key) - CurrencyWallet.Get(before, group.Key) != group.Sum(row => (long)row.Count))
                return false;
        foreach (var group in rewards.Where(row => pieces.ByPiece.ContainsKey(row.ItemId))
            .GroupBy(row => pieces.ByPiece[row.ItemId].Index))
            if ((long)after.EpicPieces.Get(group.Key) - before.EpicPieces.Get(group.Key) != group.Sum(row => (long)row.Count))
                return false;
        return true;
    }
}
