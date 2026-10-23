using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.ExpertJob;

internal sealed class ExpertJobEnchanting(ExpertJobScriptCatalog scripts, ItemCatalog items, EnchantmentCatalog enchantments,
    ExpertJobCompatibility compatibility, TimeProvider? clock = null, Func<int, int>? random = null)
{
    readonly ExpertJobCompatibility rules = compatibility.Validate();
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly Func<int, int> next = random ?? Random.Shared.Next;

    public ExpertJobActionPlan? Plan(Character character, int recipeId, InventorySlot targetSlot, InventorySlot sourceSlot)
    {
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        var source = character.Inventory.Find(sourceSlot);
        var target = character.Inventory.Find(targetSlot);
        if (character.ExpertJobType != 1 || sourceSlot.List != InventoryListType.Main || targetSlot.List != InventoryListType.Main ||
            sourceSlot == targetSlot || source is null || target is null || source.Uid <= 0 || target.Uid <= 0 ||
            !source.Core.IsStackable || source.Core.Count <= 0 || target.Core.IsStackable || target.Core.Kind != ItemKind.Equipment ||
            source.Core.EquipmentLockId != 0 || target.Core.EquipmentLockId != 0 || source.IsExpired(now) || target.IsExpired(now) ||
            !scripts.TryCardRecipe(source.Core.ItemId, out var recipe) || recipe.RecipeId != recipeId ||
            !character.ExpertJobRecipes.Contains(recipeId) || scripts.Level(1, character.ExpertJobExperience) < recipe.RequiredLevel ||
            character.ExpertMachineEndurance < rules.EnchantEnduranceCost) return null;
        var sourceTemplate = items.Get(source.Core.ItemId);
        var targetTemplate = items.Get(target.Core.ItemId);
        var definition = enchantments.Get(recipe.BeadId);
        if (definition is null || definition.CardItemId != source.Core.ItemId ||
            target.Core.EnchantCardId == source.Core.ItemId && target.Core.EnchantUpgrade == definition.Upgrade || sourceTemplate.UseForbidden ||
            character.Level < sourceTemplate.MinimumLevel || (sourceTemplate.Jobs & (1 << character.Job)) == 0 ||
            sourceTemplate.Kind != source.Core.Kind || targetTemplate.Kind != target.Core.Kind ||
            !definition.Targets.Contains((targetTemplate.Kind, targetTemplate.EquipmentSlot)) ||
            !definition.LimitedItems.IsEmpty && !definition.LimitedItems.Contains(targetTemplate.Id) ||
            !ItemSlotBoundService.Allows(character.Inventory, sourceSlot, source.Core.Kind) ||
            !ItemSlotBoundService.Allows(character.Inventory, targetSlot, target.Core.Kind)) return null;
        try
        {
            var inventory = InventoryDeleteService.Plan(character.Inventory, sourceSlot, source.Core.ItemId, 1).Plan!.After;
            long gold = rules.EnchantGoldCost;
            long? deadline = MinDeadline(source.ExpireTime, target.ExpireTime);
            foreach (var material in recipe.Materials)
            {
                if (material.ItemId == 0) { gold = checked(gold + material.Count); continue; }
                if (CurrencyWallet.RewardKind(material.ItemId) is { } currency)
                {
                    var paidCurrency = CurrencyService.Spend(inventory, currency, material.Count);
                    if (!paidCurrency.Accepted || paidCurrency.Plan is null) return null;
                    inventory = paidCurrency.Plan.After;
                    continue;
                }
                int remaining = material.Count;
                foreach (var item in inventory.Items.Where(row => row.Position.List == InventoryListType.Main &&
                    row.Position != sourceSlot && row.Position != targetSlot && row.Core.ItemId == material.ItemId && row.Core.IsStackable &&
                    row.Core.EquipmentLockId == 0 && !row.IsExpired(now)).OrderBy(row => row.Position.Index).ToArray())
                {
                    int take = Math.Min(remaining, item.Core.Count);
                    var paid = InventoryDeleteService.Plan(inventory, item.Position, item.Core.ItemId, take);
                    if (!paid.Accepted || paid.Plan is null) return null;
                    inventory = paid.Plan.After;
                    if (item.ExpireTime > 0) deadline = deadline is null ? item.ExpireTime : Math.Min(deadline.Value, item.ExpireTime);
                    remaining -= take;
                    if (remaining == 0) break;
                }
                if (remaining != 0) return null;
            }
            if (gold > inventory.Gold) return null;
            inventory = inventory with { Gold = inventory.Gold - checked((uint)gold) };
            int chance = rules.EnchantSuccessPercent ?? scripts.CardSuccessPercent(scripts.Level(1, character.ExpertJobExperience), recipe.Rarity);
            bool success = next(100) < chance;
            int experience = character.ExpertJobExperience;
            if (success)
            {
                var changed = target with { Core = target.Core with { EnchantCardId = source.Core.ItemId, EnchantUpgrade = definition.Upgrade } };
                inventory = inventory with { Items = inventory.Items.Replace(target, changed) };
                var gain = scripts.MonsterCardExp(1, scripts.Level(1, character.ExpertJobExperience));
                experience = checked(experience + gain.Min + (gain.Max > gain.Min ? next(gain.Max - gain.Min + 1) : 0));
            }
            inventory.Validate();
            return new(character, new(character.Inventory, inventory, "expert_job_card_enchant", new(character, deadline, time)),
                experience, character.ExpertMachineEndurance - rules.EnchantEnduranceCost, success, EnchantRarity: recipe.Rarity);
        }
        catch (OverflowException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    static long? MinDeadline(long first, long second) => first > 0 ? second > 0 ? Math.Min(first, second) : first : second > 0 ? second : null;
}
