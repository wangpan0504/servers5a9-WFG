using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed record RecipeConsumedItem(InventorySlot Position, int Count);
internal sealed record RecipeProducedItem(InventorySlot Position, int ItemId, int Count);
internal sealed record RecipeCraftReceipt(int RecipeId, ushort Amount, bool Success,
    ImmutableArray<RecipeConsumedItem> Consumed, ImmutableArray<RecipeProducedItem> Products, ItemMakingSkillGain? MakingGain);

internal sealed class InventoryRecipeCraftService(ItemRecipeCatalog recipes, ItemCatalog items, EpicPieceCatalog pieces,
    GoldLimitCatalog goldLimits, ItemMakingSkillCatalog makingSkills, TimeProvider? clock = null, Func<long, long>? random = null)
{
    internal const ushort Rejection = 179;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    readonly Func<long, long> draw = random ?? Random.Shared.NextInt64;

    public InventoryResult Craft(Character character, uint source, byte mode, ushort amount,
        out RecipeCraftReceipt? receipt, DateTimeOffset? utcNow = null)
    {
        receipt = null;
        InventoryResult Reject(ushort error, string reason) => InventoryResult.Reject(error) with { RejectReason = reason };
        if (mode > 1 || amount == 0 || source == 0 || source > int.MaxValue || mode == 0 && source > short.MaxValue)
            return Reject(Rejection, "invalid-source-or-count");
        var before = character.Inventory;
        var now = utcNow ?? Clock.GetUtcNow();
        long seconds = now.ToUnixTimeSeconds();
        InventoryItem? blueprint = null;
        int recipeId;
        if (mode == 0)
        {
            blueprint = before.Find(new(InventoryListType.Main, (short)source));
            if (blueprint is not { Uid: > 0, Core.Kind: ItemKind.Consumable } || blueprint.Core.Count < amount ||
                blueprint.Core.EquipmentLockId != 0 || blueprint.IsExpired(seconds) ||
                !ItemSlotBoundService.Allows(before, blueprint.Position, blueprint.Core.Kind))
                return Reject(Rejection, "invalid-blueprint-instance");
            recipeId = blueprint.Core.ItemId;
        }
        else recipeId = (int)source;
        var definition = recipes.Get(recipeId);
        if (definition is null || mode == 1 && !definition.CatalogDirect ||
            !items.Get(recipeId).AllowsUse(character.Level, character.Job) ||
            mode == 0 && definition.Skills.Any(required => !character.Skills.Any(skill => skill.Id == required.ItemId && skill.Level >= required.Count)) ||
            definition.MakingType is not null && !makingSkills.Available)
            return Reject(Rejection, "recipe-resource-or-qualification");

        var outputs = new List<InventoryReward>();
        int equipmentCount = 0;
        foreach (var output in definition.Outputs)
        {
            long count = (long)output.Count * amount;
            var template = items.Get(output.ItemId);
            if (count is <= 0 or > int.MaxValue || template.Kind is not (ItemKind.Equipment or ItemKind.Consumable or ItemKind.Material) ||
                template.IsPremiumService || template.AcquisitionContract is not null || pieces.ByPiece.ContainsKey(output.ItemId))
                return Reject(Rejection, "unsupported-output");
            if (template.Kind == ItemKind.Equipment)
            {
                if (count > 1 || ++equipmentCount > 1) return Reject(23, "multiple-equipment-output");
            }
            outputs.Add(new(output.ItemId, (int)count));
        }

        var paid = before;
        if (blueprint is not null)
        {
            var removed = InventoryDeleteService.Plan(paid, blueprint.Position, blueprint.Core.ItemId, amount);
            if (!removed.Accepted) return Reject(19, "blueprint-count");
            paid = removed.Plan!.After;
        }
        long gold = definition.Inputs.Where(row => row.ItemId == 0).Sum(row => (long)row.Count * amount);
        if (gold > int.MaxValue || gold > paid.Gold) return Reject(14, "insufficient-gold");
        paid = paid with { Gold = checked(paid.Gold - (uint)gold) };

        InventoryItem? preserved = null;
        var costs = definition.Inputs.Where(row => row.ItemId != 0).ToArray();
        if (definition.MaintainAbility == "normal" || definition.MaintainSocketStat)
        {
            if (amount != 1 || definition.Outputs.Length != 1 || equipmentCount != 1 || costs.Length == 0 || costs[0].Count != 1)
                return Reject(Rejection, "invalid-preserved-equipment-recipe");
            preserved = paid.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == costs[0].ItemId &&
                item.Core.Kind == ItemKind.Equipment && item.Avatar is null && item.Creature is null && item.Uid > 0 &&
                item.Core.EquipmentLockId == 0 && !item.IsExpired(seconds)).OrderBy(item => item.Position.Index).FirstOrDefault();
            if (preserved is null) return Reject(19, "missing-preserved-equipment");
            costs = costs.Skip(1).ToArray();
        }
        var spent = InventoryMaterialConsumptionService.Plan(paid, costs, pieces, now, amount);
        if (!spent.Accepted) return Reject(19, "insufficient-material");
        paid = spent.Plan!.After;
        if (preserved is not null)
        {
            var removed = InventoryDeleteService.Plan(paid, preserved.Position, preserved.Core.Value, 1);
            if (!removed.Accepted) return Reject(19, "preserved-equipment-changed");
            paid = removed.Plan!.After;
        }
        foreach (var group in outputs.Where(output => CurrencyWallet.RewardKind(output.ItemId) is not null)
            .GroupBy(output => CurrencyWallet.RewardKind(output.ItemId)!.Value))
        {
            if (ItemSlotBoundService.CurrencySlot(group.Key) is null ||
                (long)CurrencyWallet.Get(paid, group.Key) + group.Sum(output => (long)output.Count) > int.MaxValue)
                return Reject(4, "output-wallet-capacity");
        }
        var consumedPlan = new InventoryPlan(before, paid, "recipe_inputs");
        var consumed = ImmutableArray.CreateBuilder<RecipeConsumedItem>();
        foreach (var position in consumedPlan.ChangedSlots)
        {
            var old = consumedPlan.BeforeIndex.Find(position)!;
            int count = old.Core.Count - (consumedPlan.AfterIndex.Find(position)?.Core.Count ?? 0);
            if (count > 0) consumed.Add(new(position, count));
        }
        foreach (var currency in consumedPlan.ChangedCurrencies)
        {
            var slot = ItemSlotBoundService.CurrencySlot(currency);
            long count = CurrencyWallet.Get(before, currency) - CurrencyWallet.Get(paid, currency);
            if (slot is null || count <= 0 || count > int.MaxValue) return Reject(Rejection, "unsupported-input-wallet");
            consumed.Add(new(new(InventoryListType.Main, slot.Value), (int)count));
        }
        if (consumed.Count > byte.MaxValue) return Reject(Rejection, "too-many-input-rows");

        InventoryResult Grant(IReadOnlyList<InventoryReward> products)
        {
            if (preserved is not null)
            {
                var updated = preserved with { Core = preserved.Core with { ItemId = products[0].ItemId } };
                return new(true, 0, new(paid, paid with { Items = paid.Items.Add(updated) }, "recipe_output"));
            }
            return InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid }, products, items, goldLimits, pieces, now);
        }
        // 容量检查先于随机，不因抽到失败而让满包请求悄悄扣掉材料。
        var grant = Grant(outputs);
        if (!grant.Accepted) return Reject(4, "output-capacity");
        bool success = definition.SuccessPercent == 0 || ItemMakingSkillCatalog.Draw(draw, 100) < definition.SuccessPercent;
        var after = paid;
        if (success)
        {
            if (preserved is null && definition.MakingType is { } type)
            {
                int level = makingSkills.Level(before.ItemMakingSkills.Points[type], character.Level);
                for (int index = 0; index < outputs.Count; index++)
                    if (items.Get(outputs[index].ItemId) is { Kind: ItemKind.Equipment, Rarity: not 5 })
                        outputs[index] = outputs[index] with { Upgrade = makingSkills.Upgrade(level, draw) };
                grant = Grant(outputs);
                if (!grant.Accepted) return Reject(4, "output-capacity-after-upgrade");
            }
            after = grant.Plan!.After;
        }
        var productPlan = new InventoryPlan(paid, after, "recipe_outputs");
        var products = ImmutableArray.CreateBuilder<RecipeProducedItem>();
        foreach (var position in productPlan.ChangedSlots)
        {
            if (productPlan.AfterIndex.Find(position) is not { } item) continue;
            var old = productPlan.BeforeIndex.Find(position);
            int count = item.Core.Count - (old?.Core.ItemId == item.Core.ItemId ? old.Core.Count : 0);
            if (count > 0) products.Add(new(position, item.Core.ItemId, count));
        }
        foreach (var currency in productPlan.ChangedCurrencies)
        {
            var slot = ItemSlotBoundService.CurrencySlot(currency);
            int itemId = definition.Outputs.First(row => CurrencyWallet.RewardKind(row.ItemId) == currency).ItemId;
            long count = CurrencyWallet.Get(after, currency) - CurrencyWallet.Get(paid, currency);
            if (slot is null || count <= 0 || count > int.MaxValue) return Reject(Rejection, "unsupported-output-wallet");
            products.Add(new(new(InventoryListType.Main, slot.Value), itemId, (int)count));
        }
        if (products.Count > byte.MaxValue) return Reject(Rejection, "too-many-output-rows");
        ItemMakingSkillGain? gained = null;
        if (definition.MakingType is not null)
        {
            var growth = ItemMakingSkillPolicy.Gain(character, definition, makingSkills, draw);
            after = after with { ItemMakingSkills = growth.State };
            gained = growth.Gain;
        }
        long? deadline = consumedPlan.ChangedSlots.Select(consumedPlan.BeforeIndex.Find).OfType<InventoryItem>()
            .Concat(productPlan.ChangedSlots.Select(productPlan.AfterIndex.Find).OfType<InventoryItem>())
            .Where(item => item.ExpireTime > 0).Select(item => (long?)item.ExpireTime).Min();
        receipt = new(recipeId, amount, success, consumed.ToImmutable(), products.ToImmutable(), gained);
        return new(true, 0, new(before, after, "recipe_craft", new(character, deadline, Clock, requirePosition: true)));
    }
}
