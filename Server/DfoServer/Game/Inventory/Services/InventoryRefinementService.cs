using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryRefinementService(ItemCatalog catalog, EquipmentRefinementCatalog refinement, Random? random = null, TimeProvider? clock = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Refine(Character character, RefinementRequest request, out RefinementReceipt? receipt, DateTimeOffset? utcNow = null,
        int guildSuccessPercent = 0)
    {
        receipt = null;
        var before = character.Inventory;
        var target = before.Find(request.Position);
        var materialSlot = new InventorySlot(InventoryListType.Main, request.MaterialSlot);
        long now = (utcNow ?? Clock.GetUtcNow()).ToUnixTimeSeconds();
        if (request.Position.List is not (InventoryListType.Main or InventoryListType.Equipment) || request.Position == materialSlot ||
            target is null || target.Core.Kind != ItemKind.Equipment || target.Core.ItemId != request.ItemId ||
            target.Core.EquipmentLockId != 0 || target.Core.IsExpired(now))
            return InventoryResult.Reject(4);
        var template = catalog.Get(target.Core.ItemId);
        if (template.EquipmentSlot != 12)
            return InventoryResult.Reject(19);
        byte oldLevel = target.Core.GenuineUpgrade;
        var material = before.Find(materialSlot);
        RefinementTicketDefinition? ticket;
        ItemTemplate? sourceTemplate;
        try
        {
            sourceTemplate = material?.Core.IsStackable == true ? catalog.Get(material.Core.ItemId) : null;
            ticket = sourceTemplate?.RefinementTicket;
        }
        catch (InvalidDataException)
        {
            return InventoryResult.Reject(22);
        }
        RefinementCost cost;
        byte desiredLevel;
        if (ticket is { Available: false }) return InventoryResult.Reject(13);
        if (ticket is { Portable: false })
        {
            if (!ticket.AllowedItemIds.IsEmpty && !ticket.AllowedItemIds.Contains(target.Core.ItemId))
                return InventoryResult.Reject(19);
            if (!ticket.TryTargetLevel(oldLevel, refinement.MaximumLevel, out desiredLevel))
                return InventoryResult.Reject(95);
            // 券类按原定义覆盖或增加等级，不要求满耐久；每次一张，不叠加普通材料费用。
            cost = new(material!.Core.ItemId, 1, ticket.SuccessWeight);
        }
        else
        {
            if (target.Core.Durability != template.Durability)
                return InventoryResult.Reject(7);
            if (oldLevel >= refinement.MaximumLevel)
                return InventoryResult.Reject(95);
            if (!refinement.TryCost(template, oldLevel, out cost))
                return InventoryResult.Reject(13);
            desiredLevel = checked((byte)(oldLevel + 1));
            if (ticket is { Portable: true })
            {
                if (!sourceTemplate!.AllowsUse(character.Level, character.Job) ||
                    !InventoryMoveService.RequiredSkillSatisfied(character, sourceTemplate) ||
                    template.MinimumLevel < ticket.MinimumItemLevel || template.MinimumLevel > ticket.MaximumItemLevel)
                    return InventoryResult.Reject(19);
                cost = new(material!.Core.ItemId, 1, cost.SuccessWeight);
            }
        }
        // 两种入口都按指定格扣除，不跨堆叠补扣；确认状态不能取代实际材料/券实例。
        if (material is null || !material.Core.IsStackable || material.Core.ItemId != cost.ItemId || material.Core.Count < cost.Count ||
            material.Core.EquipmentLockId != 0 || material.Core.IsExpired(now))
            return InventoryResult.Reject(22);
        var paid = InventoryDeleteService.Plan(before, materialSlot, material.Core.Value, cost.Count);
        if (!paid.Accepted)
            return InventoryResult.Reject(17);
        // 普通材料和便携炉共用锻造概率；直接等级券保持券本身的成功率。
        int successWeight = ticket is null or { Portable: true }
            ? InventoryEnhancementService.ApplyGuildSuccessBonus(cost.SuccessWeight, guildSuccessPercent) : cost.SuccessWeight;
        bool succeeded = (random ?? Random.Shared).Next(10000) < successWeight;
        byte newLevel = succeeded ? desiredLevel : oldLevel;
        var after = paid.Plan!.After;
        if (succeeded)
            after = after with { Items = after.Items.Replace(target, target with { Core = target.Core with { GenuineUpgrade = newLevel } }) };
        receipt = new(request.MaterialSlot, material.Core.Count - cost.Count, oldLevel, succeeded, newLevel, request.Position)
        {
            Target = after.Find(request.Position)!, SourceItemId = material.Core.ItemId, UsedTicket = ticket is not null
        };
        long? deadline = InventoryAvatarPackageService.EarliestDeadline(target, [material]);
        // 失败抽签及同级券也消费材料；目标未产生差量时仍复验原实例的期限。
        return new(true, 0, new(before, after, "equipment_refinement", new(character, deadline, Clock, requirePosition: true)));
    }
}
