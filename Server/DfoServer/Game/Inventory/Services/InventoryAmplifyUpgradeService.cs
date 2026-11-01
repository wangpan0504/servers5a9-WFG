using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryAmplifyUpgradeService(ItemCatalog items, AmplifyUpgradeCatalog upgrades, EnhancementCatalog enhancements,
    DisjointCatalog disjoint, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, TownCatalog towns,
    Random? random = null, TimeProvider? clock = null, RecoveryConsumableCatalog? consumables = null)
{
    const ushort RejectCode = InventoryEnhancementService.Rejection;
    public InventoryResult Plan(Character character, EnhancementRequest request, out EnhancementReceipt? receipt, int guildSuccessPercent = 0)
    {
        receipt = null;
        var before = character.Inventory;
        var sourcePosition = new InventorySlot(InventoryListType.Main, request.MaterialSlot);
        var source = before.Find(sourcePosition);
        var target = before.Find(request.Target);
        long now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        bool reinforce = request.Mode == EnhancementMode.Reinforce;
        if (request.Mode is not (EnhancementMode.Amplify or EnhancementMode.LuckyAmplify or EnhancementMode.Reinforce) ||
            request.Target.List is not (InventoryListType.Main or InventoryListType.Equipment) || request.Target == sourcePosition ||
            request.ProtectionSlot != -1 || request.AuxiliarySlot0 != -1 || request.AuxiliarySlot1 != -1 ||
            source is null || target is null || source.Uid <= 0 || target.Uid <= 0 || source.Uid == target.Uid || !source.Core.IsStackable ||
            target.Core.ItemId != request.TargetItemId || target.Core.Kind != ItemKind.Equipment || target.Core.IsRental ||
            source.Core.EquipmentLockId != 0 || target.Core.EquipmentLockId != 0 || source.IsExpired(now) || target.IsExpired(now) ||
            (reinforce ? target.Core.AmplifyType != 0 : target.Core.AmplifyType is < 1 or > 4 || target.Core.AmplifyValue == 0) ||
            target.Core.Upgrade >= 31 ||
            !ItemSlotBoundService.Allows(before, source.Position, source.Core.Kind) || !ItemSlotBoundService.Allows(before, target.Position, target.Core.Kind))
            return InventoryResult.Reject(RejectCode);
        var template = items.Get(target.Core.ItemId);
        var sourceTemplate = items.Get(source.Core.ItemId);
        if (sourceTemplate.Kind != source.Core.Kind || template.Kind != target.Core.Kind || sourceTemplate.UseForbidden ||
            template.EquipmentSlot is not (12 or >= 14 and <= 23 or 25) || target.Core.Durability != template.Durability ||
            !enhancements.Allows(template.Id, request.Mode))
            return InventoryResult.Reject(RejectCode);

        var use = upgrades.Get(source.Core.ItemId);
        bool lucky = request.Mode == EnhancementMode.LuckyAmplify || reinforce && use?.Kind == AmplifyUseKind.Lucky;
        byte old = target.Core.Upgrade;
        if (use?.PendingRule is not null || use is not null && use.Reinforce != reinforce ||
            reinforce && use is null ||
            (lucky ? use?.Kind != AmplifyUseKind.Lucky : use is { Kind: not AmplifyUseKind.Portable }))
            return InventoryResult.Reject(RejectCode);
        if (use is not null && !AllowsUse(use, character, target, template))
            return InventoryResult.Reject(RejectCode);
        // 便携强化读取普通normal表；幸运强化仍只使用自己的三段权重。
        var rules = reinforce && !lucky ? upgrades.ReinforceRules : upgrades.Rules;
        if (old >= rules.Steps.Length || rules.MaximumLevels.TryGetValue(template.Rarity, out int maximum) && old >= maximum)
            return InventoryResult.Reject(RejectCode);
        var step = rules.Steps[old];
        bool portable = use?.Kind == AmplifyUseKind.Portable;
        int failureWeight = AdjustedFailureWeight(step.FailureWeight, portable ? use!.SuccessBonusWeight : 0,
            lucky ? 0 : guildSuccessPercent);
        if (!lucky && consumables is not null)
            failureWeight = 100000 - EnhancementPotionPolicy.AdjustedSuccessWeight(100000 - failureWeight,
                EnhancementPotionPolicy.ActiveBonusWeight(before.ConsumableStates, consumables.EnhancementPotions,
                    reinforce ? EnhancementPotionMode.Reinforce : EnhancementPotionMode.Amplify,
                    (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds()));
        if (!portable && !lucky && source.Core.ItemId != step.MaterialId)
            return InventoryResult.Reject(RejectCode);
        int count = portable || lucky ? 1 : step.MaterialCount;
        int gold = portable || lucky ? 0 : rules.Gold(template);
        if (source.Core.Count < count || before.Gold < gold)
            return InventoryResult.Reject(RejectCode);
        var paid = InventoryDeleteService.Plan(before, source.Position, source.Core.ItemId, count).Plan!.After with { Gold = before.Gold - (uint)gold };
        byte penalty = lucky ? (byte)2 : rules.Penalty(template, old);
        InventoryItem? protection = null;
        AmplifyUse? protectionRule = null;
        InventorySnapshot? destroyed = null;
        ImmutableArray<EnhancementRefund> refunds = [];
        var rng = random ?? Random.Shared;
        if (!lucky && penalty == 3 && failureWeight > 0)
        {
            bool unresolvedProtection = false;
            foreach (var candidate in before.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.IsStackable &&
                item.Uid != source.Uid && item.Core.EquipmentLockId == 0 && !item.IsExpired(now)).OrderBy(item => item.Position.Index))
                if (upgrades.Get(candidate.Core.ItemId) is { Kind: AmplifyUseKind.Protection } rule && rule.Reinforce == reinforce)
                {
                    if (rule.PendingRule is not null) { unresolvedProtection = true; continue; }
                    if (AllowsUse(rule, character, target, template) && !items.Get(candidate.Core.ItemId).UseForbidden)
                    { protection = candidate; protectionRule = rule; break; }
                }
            if (protection is null && unresolvedProtection) return InventoryResult.Reject(RejectCode);
            if (protection is null)
            {
                // 先规划完整销毁补偿及容量，再抽本次升级结果，不能靠满包拒绝失败来免费重抽。
                var rewards = DisjointResultCalculator.Calculate(disjoint, template, rng,
                    grantSouls: !target.Core.DestructionSoulForbidden).ToList();
                foreach (var bonus in reinforce ? new[] { rules.Bonus } : new[] { rules.Bonus, upgrades.CompanionBonus })
                    if (bonus.Count(template, old) is > 0 and var bonusCount) rewards.Add(new(bonus.ItemId, bonusCount));
                var released = paid with { Items = paid.Items.Remove(target) };
                foreach (var reward in rewards)
                    if (CurrencyWallet.RewardKind(reward.ItemId) is { } currency &&
                        (long)CurrencyWallet.Get(released, currency) + rewards.Where(row => row.ItemId == reward.ItemId).Sum(row => (long)row.Count) > int.MaxValue)
                        return InventoryResult.Reject(RejectCode);
                var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = released }, rewards, items, goldLimits, pieces,
                    DateTimeOffset.FromUnixTimeSeconds(now));
                if (!grant.Accepted) return InventoryResult.Reject(RejectCode);
                destroyed = grant.Plan!.After;
                refunds = RefundRows(released, destroyed, rewards);
                if (refunds.Length > byte.MaxValue) return InventoryResult.Reject(RejectCode);
            }
        }
        int rolled = rng.Next(100000);
        if (rolled is < 0 or >= 100000) throw new InvalidOperationException("增幅随机源越界");
        var outcome = lucky ? LuckyOutcome((reinforce ? upgrades.LuckyReinforce : upgrades.Lucky)[old], rolled) :
            Outcome(old, rolled >= failureWeight, penalty, step.PenaltyValue);
        short protectionSlot = -1;
        int protectionRemaining = 0;
        if (outcome.Code == 3 && protection is not null)
        {
            paid = InventoryDeleteService.Plan(paid, protection.Position, protection.Core.ItemId, 1).Plan!.After;
            protectionSlot = protection.Position.Index;
            protectionRemaining = protection.Core.Count - 1;
            outcome = (2, (byte)Math.Min(old, protectionRule!.RetainLevel));
        }
        InventorySnapshot after;
        if (outcome.Code == 3)
            after = destroyed ?? throw new InvalidOperationException("销毁结果缺少预先规划的补偿");
        else
        {
            var changed = target with { Core = target.Core with { Attr = (byte)((target.Core.Attr & 0xE0) | outcome.Level) } };
            after = paid with { Items = paid.Items.Replace(target, changed) };
            refunds = [];
        }
        after.Validate(allocating: true);
        long? deadline = new[] { source.ExpireTime, target.ExpireTime, protectionSlot >= 0 ? protection!.ExpireTime : 0 }
            .Where(value => value > 0).Select(value => (long?)value).Min();
        receipt = new(reinforce ? EnhancementMode.Reinforce : EnhancementMode.Amplify,
            request.Target, request.MaterialSlot, source.Core.Count - count, old, outcome.Level, outcome.Code == 0)
        { OutcomeCode = outcome.Code, Path = lucky ? (byte)4 : (byte)0, GoldCost = gold, InPlace = true, UsesLevelProbability = !lucky,
            TerminalSlot = protectionSlot, TerminalRemaining = protectionRemaining, Refunds = refunds };
        return new(true, 0, new(before, after, reinforce ? lucky ? "equipment_reinforcement_lucky" : "equipment_reinforcement_portable" : lucky ? "equipment_amplification_lucky" : portable ? "equipment_amplification_portable" :
            "equipment_amplification_npc", new(character, deadline, clock)));
    }

    internal static int AdjustedFailureWeight(int failureWeight, int successBonusWeight, int guildSuccessPercent = 0)
    {
        if (failureWeight is < 0 or > 100000 || successBonusWeight < 0 || guildSuccessPercent < 0)
            throw new InvalidDataException("增幅失败权重或成功加成无效");
        // 第三参数30000表示基础成功权重乘1.3，不是直接增加30个百分点。
        long successWeight = (100000L - failureWeight) * (100000L + successBonusWeight) / 100000;
        // 公会加成在原便携器加成之后按同一相对倍率计算，不叠加公共与个人福利。
        successWeight = Math.Min(100000, successWeight) * (100L + guildSuccessPercent) / 100;
        return 100000 - (int)Math.Min(100000, successWeight);
    }

    internal static (byte Code, byte Level) Outcome(byte old, bool success, byte penalty, int loss) => success ? ((byte)0, (byte)(old + 1)) :
        penalty switch { 1 => ((byte)1, old), 2 => ((byte)2, (byte)Math.Max(0, old - loss)), 3 => ((byte)3, (byte)0),
            _ => throw new InvalidDataException("未知增幅失败政策") };
    internal static (byte Code, byte Level) LuckyOutcome(LuckyAmplifyStep row, int roll)
    {
        if (roll < row.DownWeight * 1000) return (2, (byte)Math.Max(0, row.Level - 1));
        if (roll < (row.DownWeight + row.SameWeight) * 1000) return (1, (byte)row.Level);
        return (0, (byte)(row.Level + 1));
    }
    static bool AllowsPart(byte part, int rule) => rule switch
    { 0 => true, 1 => part == 12, 2 => part is >= 14 and <= 18, 3 => part is >= 19 and <= 21, _ => false };

    bool AllowsUse(AmplifyUse use, Character character, InventoryItem target, ItemTemplate template) =>
        target.Core.Upgrade >= use.MinimumUpgrade && target.Core.Upgrade <= use.MaximumUpgrade &&
        template.MinimumLevel >= use.MinimumItemLevel && template.MinimumLevel <= use.MaximumItemLevel &&
        (!use.SealedOnly || target.Core.SealFlag != 0) &&
        (use.Rarities is not { Count: > 0 } rarities || rarities.Contains(template.Rarity)) && AllowsPart(template.EquipmentSlot, use.SlotClass) &&
        (use.Village || use.SeriaRoom && towns.IsRoom(character.Position, character.CompletedQuests.ToHashSet()));

    static ImmutableArray<EnhancementRefund> RefundRows(InventorySnapshot before, InventorySnapshot after, IReadOnlyList<InventoryReward> rewards)
    {
        var rows = ImmutableArray.CreateBuilder<EnhancementRefund>();
        foreach (int id in rewards.Select(row => row.ItemId).Distinct())
            if (CurrencyWallet.RewardKind(id) is { } currency)
                rows.Add(new(ItemSlotBoundService.CurrencySlot(currency) ?? throw new InvalidDataException("增幅补偿缺少货币槽"),
                    id, checked((int)(CurrencyWallet.Get(after, currency) - CurrencyWallet.Get(before, currency)))));
            else foreach (var item in after.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == id &&
                item.Core.Count > (before.Find(item.Position)?.Core.Count ?? 0)))
                rows.Add(new(item.Position.Index, id, item.Core.Count - (before.Find(item.Position)?.Core.Count ?? 0)));
        return rows.ToImmutable();
    }
}
