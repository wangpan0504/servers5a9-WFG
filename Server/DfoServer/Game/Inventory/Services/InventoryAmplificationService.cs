using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryAmplificationService(ItemCatalog items, AmplificationCatalog amplifications,
    EnhancementCatalog enhancements, TownCatalog towns, TimeProvider? clock = null)
{
    internal const ushort Rejection = 17;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Plan(Character character, AmplificationRequest request, out AmplificationReceipt? receipt)
    {
        receipt = null;
        var source = character.Inventory.Find(MainSlot(request.MaterialSlot));
        var target = character.Inventory.Find(MainSlot(request.TargetSlot));
        if (source is null || target is null || source.Core.ItemId != request.MaterialItemId || target.Core.ItemId != request.TargetItemId)
            return InventoryResult.Reject(Rejection);
        var definition = amplifications.Get(request.Operation, source.Core.ItemId);
        if (definition is null) return InventoryResult.Reject(Rejection);
        return Plan(character, request, Clock.GetUtcNow().ToUnixTimeSeconds(), definition, amplifications.Rules,
            items.Get(source.Core.ItemId), items.Get(target.Core.ItemId), enhancements.Allows(target.Core.ItemId, EnhancementMode.Amplify),
            amplifications.UsesCharacterLevel(target.Core.ItemId), !definition.Village && definition.SeriaRoom &&
                towns.IsRoom(character.Position, character.CompletedQuests.ToHashSet()),
            Random.Shared.NextInt64, out receipt);
    }

    internal static InventoryResult Plan(Character character, AmplificationRequest request, long now,
        AmplificationMaterial definition, AmplificationRules rules, ItemTemplate sourceTemplate, ItemTemplate targetTemplate,
        bool allowsTarget, bool usesCharacterLevel, bool inRoom, Func<long, long> roll, out AmplificationReceipt? receipt)
    {
        receipt = null;
        var before = character.Inventory;
        var source = before.Find(MainSlot(request.MaterialSlot));
        var target = before.Find(MainSlot(request.TargetSlot));
        bool purify = request.Operation == AmplificationOperation.Purify;
        if (source is null || target is null || source.Uid <= 0 || target.Uid <= 0 || source.Uid == target.Uid ||
            source.Core.ItemId != request.MaterialItemId || target.Core.ItemId != request.TargetItemId ||
            !source.Core.IsStackable || source.Core.Count < definition.Count || definition.Count <= 0 ||
            source.Core.EquipmentLockId != 0 || target.Core.EquipmentLockId != 0 || source.IsExpired(now) || target.IsExpired(now) ||
            sourceTemplate.Id != source.Core.ItemId || sourceTemplate.Kind != source.Core.Kind ||
            targetTemplate.Id != target.Core.ItemId || targetTemplate.Kind != target.Core.Kind || target.Core.Kind != ItemKind.Equipment ||
            sourceTemplate.UseForbidden ||
            !ItemSlotBoundService.Allows(before, source.Position, source.Core.Kind) || !ItemSlotBoundService.Allows(before, target.Position, target.Core.Kind) ||
            !(definition.Village || inRoom && definition.SeriaRoom) ||
            (purify ? definition.Operation is not (AmplificationOperation.Purify or AmplificationOperation.Clear or AmplificationOperation.GoldClear) :
                request.Operation != definition.Operation))
            return InventoryResult.Reject(Rejection);
        int level = usesCharacterLevel ? character.Level : targetTemplate.MinimumLevel;
        if (!purify && !allowsTarget || target.Core.IsRental || targetTemplate.EquipmentSlot is not (12 or >= 14 and <= 23 or 25) ||
            targetTemplate.Rarity < 2 || !rules.GradeCorrections.TryGetValue(targetTemplate.Rarity, out int correction) ||
            level < rules.MinimumLevel || Math.Max(0, targetTemplate.Grade - correction) < rules.MinimumLevel ||
            targetTemplate.MinimumLevel < definition.MinimumItemLevel || targetTemplate.MinimumLevel > definition.MaximumItemLevel ||
            !purify && target.Core.Durability < targetTemplate.Durability)
            return InventoryResult.Reject(8);

        byte oldType = target.Core.AmplifyType, type = (byte)(oldType & 0x7F), resultType;
        ushort value;
        bool unidentified = (oldType & 0x80) != 0;
        byte newLevel = target.Core.Upgrade;
        if (oldType != 0 && (type is < 1 or > 4 || target.Core.AmplifyValue == 0) || oldType == 0 && target.Core.AmplifyValue != 0)
            return InventoryResult.Reject(Rejection);
        switch (definition.Operation)
        {
            case AmplificationOperation.Purify:
                if (!unidentified) return InventoryResult.Reject(23);
                // 净化只移除未识别位；掉落生成时已有的属性及基础值不能重新随机。
                resultType = type;
                value = target.Core.AmplifyValue;
                break;
            case AmplificationOperation.Clear:
            case AmplificationOperation.GoldClear:
                if (oldType == 0 || definition.Operation == AmplificationOperation.Clear && !unidentified)
                    return InventoryResult.Reject(23);
                resultType = 0;
                value = 0;
                if (definition.Operation == AmplificationOperation.GoldClear) newLevel = 0;
                break;
            default:
                if (unidentified) return InventoryResult.Reject(21);
                if (definition.Operation == AmplificationOperation.Invest && oldType != 0) return InventoryResult.Reject(20);
                if (definition.Operation == AmplificationOperation.Twist && oldType == 0) return InventoryResult.Reject(21);
                if (definition.Operation != AmplificationOperation.GoldInvest && target.Core.Upgrade != 0) return InventoryResult.Reject(18);
                if (definition.Option == 5)
                {
                    if (request.SelectedOption is < 1 or > 4) return InventoryResult.Reject(Rejection);
                    resultType = request.SelectedOption;
                }
                else
                {
                    if (definition.Option is < 1 or > 4 || request.SelectedOption != 0) return InventoryResult.Reject(Rejection);
                    resultType = definition.Option;
                }
                if (resultType == type) return InventoryResult.Reject(23);
                value = rules.BaseValue(resultType, targetTemplate.Rarity);
                if (value == 0) return InventoryResult.Reject(8);
                if (definition.Operation == AmplificationOperation.GoldInvest)
                {
                    long total = 0;
                    foreach (var row in definition.Levels)
                    {
                        if (row.Level > 31 || row.Weight < 0) return InventoryResult.Reject(Rejection);
                        total = checked(total + row.Weight);
                    }
                    if (total <= 0) return InventoryResult.Reject(Rejection);
                    long selected = roll(total);
                    if (selected < 0 || selected >= total) throw new InvalidOperationException("黄金增幅书随机源越界");
                    foreach (var row in definition.Levels)
                    {
                        if (selected < row.Weight) { newLevel = row.Level; break; }
                        selected -= row.Weight;
                    }
                }
                break;
        }
        var consumed = InventoryDeleteService.Plan(before, source.Position, source.Core.ItemId, definition.Count);
        if (!consumed.Accepted) return InventoryResult.Reject(Rejection);
        var changed = target with { Core = target.Core with { AmplifyType = resultType, AmplifyValue = value,
            Attr = (byte)((target.Core.Attr & 0xE0) | newLevel) } };
        var after = consumed.Plan!.After with { Items = consumed.Plan.After.Items.Replace(target, changed) };
        after.Validate();
        long? deadline = source.ExpireTime > 0 ? source.ExpireTime : null;
        if (target.ExpireTime > 0) deadline = deadline is null ? target.ExpireTime : Math.Min(deadline.Value, target.ExpireTime);
        receipt = new(definition.Operation, request.MaterialSlot, source.Core.Count - definition.Count, request.TargetSlot,
            resultType, value, newLevel, definition.Operation == AmplificationOperation.GoldClear && target.Core.Upgrade != 0);
        return new(true, 0, new(before, after, "equipment_amplification_option", new(character, deadline)));
    }

    static InventorySlot MainSlot(short slot) => new(InventoryListType.Main, slot);
}
