using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryMonsterCardBindService(ItemCatalog items, MonsterCardBindCatalog compounds,
    GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, Func<long, long>? random = null, TimeProvider? clock = null)
{
    const ushort Rejection = 22;
    const ushort InventoryFull = 23;
    readonly Func<long, long> draw = random ?? Random.Shared.NextInt64;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Apply(Character character, MonsterCardBindRequest request,
        out MonsterCardBindReceipt? receipt, out string? rejection, DateTimeOffset? utcNow = null)
    {
        receipt = null;
        rejection = null;
        var plannedAt = Clock.GetUtcNow();
        var now = utcNow ?? plannedAt;
        long seconds = now.ToUnixTimeSeconds();
        var before = character.Inventory;
        if (request.CardSlots.IsDefaultOrEmpty || request.CardSlots.Any(slot => slot is < 0 or > 351 || slot == request.SynthesizerSlot))
            return Reject("invalid-slots", out rejection);
        if (before.Find(request.SynthesizerPosition) is not { } synthesizer ||
            synthesizer.Position.List != InventoryListType.Main || synthesizer.Core.ItemId <= 0 ||
            synthesizer.Core.Count < 1 || synthesizer.Core.EquipmentLockId != 0 || synthesizer.IsExpired(seconds))
            return Reject("synthesizer-state", out rejection);
        ItemTemplate synthesizerTemplate;
        try { synthesizerTemplate = items.Get(synthesizer.Core.ItemId); }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            return Reject("synthesizer-metadata", out rejection);
        }
        if (synthesizerTemplate.Id != synthesizer.Core.ItemId || synthesizerTemplate.MonsterCardBindType is not { } bindType ||
            !ItemSlotBoundService.Allows(before, synthesizer.Position, synthesizerTemplate.Kind))
            return Reject("synthesizer-template", out rejection);
        bool sphere = MonsterCardBindCatalog.IsSphere(bindType);
        if (request.CardSlots.Length is < 2 or > 5)
            return Reject("invalid-count", out rejection);

        var cards = new List<InventoryItem>(request.CardSlots.Length);
        int inputRarity = -1;
        foreach (short slot in request.CardSlots)
        {
            var position = new InventorySlot(InventoryListType.Main, slot);
            InventoryItem? source = before.Find(position);
            if (source is null || source.Position != position || source.Core.ItemId <= 0 || source.Core.Count < 1 ||
                source.Core.EquipmentLockId != 0 || source.Core.EnchantUpgrade != 0 || source.IsExpired(seconds))
                return Reject("material-state", out rejection);
            ItemTemplate template;
            try { template = items.Get(source.Core.ItemId); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
            {
                return Reject("material-metadata", out rejection);
            }
            if (template.Id != source.Core.ItemId || !template.IsMonsterCard || template.MonsterCardBindType is not null ||
                !ItemSlotBoundService.Allows(before, position, template.Kind))
                return Reject("material-template", out rejection);
            if (inputRarity < 0)
                inputRarity = template.Rarity;
            else if (template.Rarity != inputRarity)
                return Reject("mixed-rarity", out rejection);
            cards.Add(source);
        }
        if (inputRarity < 0)
            return Reject("material-state", out rejection);
        foreach (var group in cards.GroupBy(card => card.Position))
            if (group.First().Core.Count < group.Count())
                return Reject("material-count", out rejection);

        int outputRarity;
        bool rarityUpgraded;
        MonsterCardBindPool pool;
        MonsterCardBindRewardDefinition selected;
        try
        {
            var inputPool = compounds.Pool(bindType, inputRarity);
            if (!sphere && request.CardSlots.Length > inputPool.MaximumCardCount)
                return Reject("invalid-count", out rejection);
            outputRarity = OutputRarity(bindType, request.CardSlots.Length, inputRarity, inputPool.Coefficient);
            rarityUpgraded = outputRarity != inputRarity;
            pool = outputRarity == inputRarity ? inputPool : compounds.Pool(bindType, outputRarity);
            selected = Pick(pool.Rewards);
            var selectedTemplate = items.Get(selected.ItemId);
            if (!selectedTemplate.IsMonsterCard || selectedTemplate.Rarity != outputRarity ||
                selectedTemplate.MonsterCardBindType is not null)
                return Reject("reward-template", out rejection);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            return Reject("reward-roll", out rejection);
        }

        var paid = before;
        var deleted = InventoryDeleteService.Plan(paid, synthesizer.Position, synthesizer.Core.ItemId, 1);
        if (!deleted.Accepted)
            return Reject("synthesizer-changed", out rejection);
        paid = deleted.Plan!.After;
        foreach (var card in cards)
        {
            deleted = InventoryDeleteService.Plan(paid, card.Position, card.Core.ItemId, 1);
            if (!deleted.Accepted)
                return Reject("material-changed", out rejection);
            paid = deleted.Plan!.After;
        }

        var granted = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid },
            [new(selected.ItemId, 1)], items, goldLimits, pieces, now);
        if (!granted.Accepted)
        {
            rejection = granted.Error == 17 ? "card-capacity" : "reward-grant";
            return InventoryResult.Reject(granted.Error == 17 ? InventoryFull : Rejection);
        }

        var after = granted.Plan!.After;
        var rewards = after.Items.Where(item => item.Core.ItemId == selected.ItemId && paid.Find(item.Position) != item)
            .Select(item => new MonsterCardBindReward(item.Position.Index, item.Core.ItemId, item.Core.Count, 0))
            .ToImmutableArray();
        if (rewards.Length == 0)
            return Reject("reward-slot", out rejection);
        long? deadline = EarliestDeadline(cards.Append(synthesizer)
            .Concat(after.Items.Where(item => rewards.Any(reward => reward.Slot == item.Position.Index))));
        receipt = new(request.SynthesizerSlot, bindType, inputRarity, outputRarity, rarityUpgraded,
            request.CardSlots, rewards);
        return new(true, 0, new InventoryPlan(before, after, "monster_card_bind",
            new InventoryCharacterPrecondition(character, deadline))
        {
            MonsterCardBind = new(Guid.NewGuid(), plannedAt, Clock, request, receipt, synthesizer, bindType,
                cards.GroupBy(card => card.Position).Select(group => new MonsterCardBindMaterial(
                    group.First(), group.Count(), inputRarity)).ToImmutableArray(), selected.ItemId, before, paid, after)
        });
    }

    int OutputRarity(int bindType, int count, int inputRarity, decimal coefficient)
    {
        if (MonsterCardBindCatalog.IsSphere(bindType))
        {
            int guaranteed = bindType switch { 3 => 1, 4 => 2, 5 => 3, _ => inputRarity };
            return count >= 5 ? Math.Max(inputRarity, guaranteed) : inputRarity;
        }
        if (inputRarity >= 3)
            return inputRarity;
        int chance = MonsterCardBindCatalog.UpgradeChance(coefficient, count);
        if (chance <= 0)
            return inputRarity;
        long roll = draw(MonsterCardBindCatalog.UpgradeRollRange);
        if (roll < 0 || roll >= MonsterCardBindCatalog.UpgradeRollRange)
            throw new InvalidDataException("怪物卡片升档随机值越界");
        return roll < chance ? inputRarity + 1 : inputRarity;
    }

    MonsterCardBindRewardDefinition Pick(IReadOnlyList<MonsterCardBindRewardDefinition> candidates)
    {
        long total = candidates.Aggregate(0L, (sum, candidate) => checked(sum + candidate.Weight));
        if (total <= 0)
            throw new InvalidDataException("怪物卡片合成奖池总权重无效");
        long value = draw(total);
        if (value < 0 || value >= total)
            throw new InvalidDataException("怪物卡片合成随机值越界");
        foreach (var candidate in candidates)
        {
            if (value < candidate.Weight)
                return candidate;
            value -= candidate.Weight;
        }
        throw new InvalidDataException("怪物卡片合成加权抽取失败");
    }

    static long? EarliestDeadline(IEnumerable<InventoryItem> assets)
    {
        long? deadline = null;
        foreach (var item in assets)
            if (item.ExpireTime > 0 && (deadline is null || item.ExpireTime < deadline.Value))
                deadline = item.ExpireTime;
        return deadline;
    }

    static InventoryResult Reject(string reason, out string? rejection)
    {
        rejection = reason;
        return InventoryResult.Reject(Rejection);
    }
}
