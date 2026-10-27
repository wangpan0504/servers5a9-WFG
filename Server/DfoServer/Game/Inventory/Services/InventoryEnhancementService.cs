using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryEnhancementService(ItemCatalog items, EnhancementCatalog enhancements, MedalUpgradeCatalog medals,
    Random? random = null, TimeProvider? clock = null, InventoryAmplifyUpgradeService? amplification = null)
{
    internal const ushort Rejection = 23;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Plan(Character character, EnhancementRequest request, out EnhancementReceipt? receipt)
    {
        receipt = null;
        var source = character.Inventory.Find(new(InventoryListType.Main, request.MaterialSlot));
        var target = character.Inventory.Find(request.Target);
        if (source is null || target is null) return InventoryResult.Reject(Rejection);
        // 勋章强化复用CMD80，但概率、材料与费用来自flag_upgrade_table；目标按种类与物品ID定位，
        // 不绑定列表38槽位：勋章页任意槽、快捷栏与穿戴槽32都属于合法位置。
        if (target.Core.Kind == ItemKind.GuildMedal) return PlanMedal(character, request, out receipt);
        if (request.Mode == EnhancementMode.LuckyAmplify)
            return amplification?.Plan(character, request, out receipt) ?? InventoryResult.Reject(Rejection);
        var definition = enhancements.Get(source.Core.ItemId);
        if (request.Mode == EnhancementMode.Reinforce && definition is { Available: false })
            return amplification?.Plan(character, request, out receipt) ?? InventoryResult.Reject(Rejection);
        if (definition is null && request.Mode == EnhancementMode.Amplify)
            return amplification?.Plan(character, request, out receipt) ?? InventoryResult.Reject(Rejection);
        if (definition is null || !definition.Available || !enhancements.Allows(target.Core.ItemId, request.Mode)) return InventoryResult.Reject(Rejection);
        return Plan(character, request, Clock.GetUtcNow().ToUnixTimeSeconds(), definition,
            items.Get(source.Core.ItemId), items.Get(target.Core.ItemId),
            () => (random ?? Random.Shared).Next(definition.Mode == EnhancementMode.RandomReinforce ? definition.RandomWeight : 10000), out receipt);
    }

    // 勋章强化：概率与惩罚取flag_upgrade_table目标等级行，材料为公会硬币，
    // 数量按稀有度权重放大后向下取整；不收金币，也没有强化券定义。
    InventoryResult PlanMedal(Character character, EnhancementRequest request, out EnhancementReceipt? receipt)
    {
        receipt = null;
        var before = character.Inventory;
        var sourcePosition = new InventorySlot(InventoryListType.Main, request.MaterialSlot);
        var source = before.Find(sourcePosition);
        var target = before.Find(request.Target);
        long now = Clock.GetUtcNow().ToUnixTimeSeconds();
        if (request.Mode != EnhancementMode.Reinforce || request.ProtectionSlot != -1 ||
            request.AuxiliarySlot0 != -1 || request.AuxiliarySlot1 != -1 ||
            source is null || target is null || source.Uid <= 0 || target.Uid <= 0 || source.Uid == target.Uid ||
            target.Core.Kind != ItemKind.GuildMedal || target.Core.ItemId != request.TargetItemId ||
            source.Core.EquipmentLockId != 0 || target.Core.EquipmentLockId != 0 || target.Core.IsRental ||
            source.IsExpired(now) || target.IsExpired(now) || !source.Core.IsStackable || source.Core.Count <= 0 ||
            !ItemSlotBoundService.Allows(before, source.Position, source.Core.Kind) ||
            !ItemSlotBoundService.Allows(before, target.Position, target.Core.Kind))
            return InventoryResult.Reject(Rejection);
        var template = items.Get(target.Core.ItemId);
        var sourceTemplate = items.Get(source.Core.ItemId);
        if (template.Id != target.Core.ItemId || template.Kind != target.Core.Kind || sourceTemplate.Id != source.Core.ItemId ||
            sourceTemplate.Kind != source.Core.Kind || sourceTemplate.UseForbidden)
            return InventoryResult.Reject(Rejection);
        var rules = medals.Rules;
        if (target.Core.Upgrade >= rules.Rows.Length) return InventoryResult.Reject(Rejection);
        var row = rules.Rows[target.Core.Upgrade];
        if (source.Core.ItemId != row.MaterialId) return InventoryResult.Reject(Rejection);
        int coins = rules.CoinCost(template.Rarity, row.MaterialCount);
        if (source.Core.Count < coins) return InventoryResult.Reject(Rejection);
        var paid = InventoryDeleteService.Plan(before, source.Position, source.Core.ItemId, coins);
        if (!paid.Accepted) return InventoryResult.Reject(Rejection);
        int rolled = (random ?? Random.Shared).Next(100000);
        if (rolled is < 0 or >= 100000) throw new InvalidOperationException("勋章强化随机源越界");
        byte oldLevel = target.Core.Upgrade;
        var outcome = InventoryAmplifyUpgradeService.Outcome(oldLevel, rolled >= row.FailureWeight, row.PenaltyType, row.PenaltyValue);
        var after = paid.Plan!.After;
        after = outcome.Code == 3 ? after with { Items = after.Items.Remove(target) } :
            after with { Items = after.Items.Replace(target, target with { Core = target.Core with { Attr = (byte)((target.Core.Attr & 0xE0) | outcome.Level) } }) };
        after.Validate();
        long? deadline = source.ExpireTime > 0 ? source.ExpireTime : null;
        if (target.ExpireTime > 0) deadline = deadline is null ? target.ExpireTime : Math.Min(deadline.Value, target.ExpireTime);
        receipt = new(EnhancementMode.Reinforce, request.Target, request.MaterialSlot, source.Core.Count - coins, oldLevel, outcome.Level, outcome.Code == 0)
        { OutcomeCode = outcome.Code, Path = 0, GoldCost = 0, InPlace = true, Refunds = [] };
        return new(true, 0, new(before, after, "guild_medal_enhancement", new(character, deadline, Clock)));
    }

    internal static InventoryResult Plan(Character character, EnhancementRequest request, long now, EnhancementDefinition definition,
        ItemTemplate sourceTemplate, ItemTemplate targetTemplate, Func<int> roll, out EnhancementReceipt? receipt)
    {
        receipt = null;
        var before = character.Inventory;
        var sourcePosition = new InventorySlot(InventoryListType.Main, request.MaterialSlot);
        var source = before.Find(sourcePosition);
        var target = before.Find(request.Target);
        if (request.Target.List is not (InventoryListType.Main or InventoryListType.Equipment) || sourcePosition == request.Target ||
            request.ProtectionSlot != -1 || request.AuxiliarySlot0 != -1 || request.AuxiliarySlot1 != -1 ||
            source is null || target is null || source.Uid <= 0 || target.Uid <= 0 || !source.Core.IsStackable || source.Core.Count <= 0 ||
            target.Core.Kind != ItemKind.Equipment || target.Core.ItemId != request.TargetItemId || target.Core.IsRental ||
            source.Core.EquipmentLockId != 0 || target.Core.EquipmentLockId != 0 || source.IsExpired(now) || target.IsExpired(now) ||
            sourceTemplate.Id != source.Core.ItemId || sourceTemplate.Kind != source.Core.Kind || targetTemplate.Id != target.Core.ItemId ||
            targetTemplate.Kind != target.Core.Kind || definition.ItemId != source.Core.ItemId || definition.Mode != request.Mode ||
            !definition.TryTargetLevel(target.Core.Upgrade, out byte desired) ||
            targetTemplate.EquipmentSlot is not (12 or >= 14 and <= 23 or 25) ||
            targetTemplate.MinimumLevel < definition.MinimumItemLevel || targetTemplate.MinimumLevel > definition.MaximumItemLevel ||
            !definition.Rarities.IsEmpty && !definition.Rarities.Contains(targetTemplate.Rarity) ||
            definition.SealedOnly && target.Core.SealFlag == 0 || !AllowsPart(targetTemplate.EquipmentSlot, definition.SlotClass) ||
            !definition.AllowedItems.IsEmpty && !definition.AllowedItems.Contains(target.Core.ItemId) ||
            !ItemSlotBoundService.Allows(before, source.Position, source.Core.Kind) || !ItemSlotBoundService.Allows(before, target.Position, target.Core.Kind) ||
            !sourceTemplate.AllowsUse(character.Level, character.Job) ||
            !InventoryMoveService.RequiredSkillSatisfied(character, sourceTemplate))
            return InventoryResult.Reject(Rejection);
        if (request.Mode is EnhancementMode.Reinforce or EnhancementMode.RandomReinforce ? target.Core.AmplifyType != 0 :
            target.Core.AmplifyType is < 1 or > 4 || target.Core.AmplifyValue == 0)
            return InventoryResult.Reject(Rejection);
        int value = roll();
        bool randomLevel = definition.Mode == EnhancementMode.RandomReinforce;
        if (value < 0 || value >= (randomLevel ? definition.RandomWeight : 10000))
            throw new InvalidOperationException("强化/增幅概率源超出范围");
        if (randomLevel)
        {
            int remaining = value;
            foreach (var row in definition.RandomLevels)
            {
                if (remaining < row.Weight)
                {
                    desired = row.Level;
                    break;
                }
                remaining -= row.Weight;
            }
        }
        bool succeeded = randomLevel || value < definition.SuccessWeight;
        byte oldLevel = target.Core.Upgrade, newLevel = succeeded ? desired : oldLevel;
        var paid = InventoryDeleteService.Plan(before, source.Position, source.Core.ItemId, 1);
        if (!paid.Accepted) return InventoryResult.Reject(Rejection);
        // Attr低五位是强化/增幅等级；AmplifyValue为基础异次元值，不能累加本次等级效果。
        var changed = target with { Core = target.Core with { Attr = (byte)((target.Core.Attr & 0xE0) | newLevel) } };
        var after = paid.Plan!.After with { Items = paid.Plan.After.Items.Replace(target, changed) };
        after.Validate();
        long? deadline = source.ExpireTime > 0 ? source.ExpireTime : null;
        if (target.ExpireTime > 0) deadline = deadline is null ? target.ExpireTime : Math.Min(deadline.Value, target.ExpireTime);
        receipt = new(request.Mode, request.Target, request.MaterialSlot, source.Core.Count - 1, oldLevel, newLevel, succeeded)
            { Path = randomLevel ? (byte)1 : (byte)0, InPlace = randomLevel };
        return new(true, 0, new(before, after, "equipment_enhancement_ticket", new(character, deadline)));
    }

    static bool AllowsPart(byte part, int rule) => rule switch
    { 0 => true, 1 => part == 12, 2 => part is >= 14 and <= 18, 3 => part is >= 19 and <= 21, _ => false };
}
