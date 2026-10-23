using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.ExpertJob;

internal sealed record ExpertJobLearningPlan(Character Expected, InventoryPlan Inventory, int RecipeId, byte? Rarity);

internal sealed class ExpertJobLearning(ExpertJobScriptCatalog scripts, ItemCatalog items, TimeProvider? clock = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    public bool Recognizes(int id) => scripts.TryLearningRecipe(id, out _);

    public ExpertJobLearningPlan? Plan(Character character, InventorySlot slot, int itemId)
    {
        if (!scripts.TryLearningRecipe(itemId, out var definition) || character.ExpertJobType != definition.Job ||
            character.ExpertJobRecipes.Count >= byte.MaxValue || character.ExpertJobRecipes.Contains(itemId) ||
            scripts.Level(character.ExpertJobType, character.ExpertJobExperience) < definition.RequiredLevel ||
            definition.Rarity is { } rarity && scripts.LearnedRarities(character.ExpertJobRecipes).Contains(rarity))
            return null;
        var source = character.Inventory.Find(slot);
        var now = Clock.GetUtcNow().ToUnixTimeSeconds();
        if (slot.List != InventoryListType.Main || source is null || source.Uid <= 0 || source.Core.ItemId != itemId ||
            !source.Core.IsStackable || source.Core.Count <= 0 || source.Core.EquipmentLockId != 0 || source.IsExpired(now))
            return null;
        var template = items.Get(itemId);
        if (template.UseForbidden || template.Kind != source.Core.Kind || character.Level < template.MinimumLevel ||
            (template.Jobs & (1 << character.Job)) == 0 || !ItemSlotBoundService.Allows(character.Inventory, slot, source.Core.Kind))
            return null;
        var consumed = InventoryDeleteService.Plan(character.Inventory, slot, itemId, 1);
        if (!consumed.Accepted || consumed.Plan is null) return null;
        var inventory = new InventoryPlan(character.Inventory, consumed.Plan.After, "expert_job_learn_recipe",
            new(character, source.ExpireTime > 0 ? source.ExpireTime : null, Clock));
        return new(character, inventory, itemId, definition.Rarity);
    }
}
