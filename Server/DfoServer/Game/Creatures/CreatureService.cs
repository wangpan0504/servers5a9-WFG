using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Creatures;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Creatures;

internal sealed class CreatureService(CreatureCatalog catalog, TimeProvider? time = null, EpicPieceCatalog? pieces = null)
{
    public CreatureCatalog Catalog { get; } = catalog;
    public TimeProvider Clock { get; } = time ?? TimeProvider.System;

    public InventoryItem? Equipped(Character character, long now)
    {
        var item = character.Inventory.Find(CreatureRules.EquippedSlot);
        if (item is not { Uid: > 0, Creature: not null, Avatar: null, Core.Kind: ItemKind.Creature } ||
            item.Core.Value != item.Uid || item.IsExpired(now) || Catalog.Get(item.Core.ItemId) is not { Egg: false }) return null;
        return item;
    }

    public InventoryResult ShiftSatiety(Character character, long units, bool revive, long now)
    {
        var item = Equipped(character, now);
        if (item?.Creature is not { } detail) return InventoryResult.Reject(123);
        return Replace(character, item, item with { Creature = CreatureRules.ShiftSatiety(detail, units, revive) }, "creature_satiety");
    }

    public InventoryResult Feed(Character character, ConsumableUseRequest request, long now)
    {
        var creature = Equipped(character, now);
        var source = character.Inventory.Find(request.Position);
        var feed = Catalog.Feed(request.ItemId);
        if (creature?.Creature is not { } detail || source is null || request.Argument != 0 ||
            source.Uid <= 0 || source.Core.ItemId != request.ItemId || source.Core.Kind is not (ItemKind.CreatureConsumable or ItemKind.Consumable) ||
            source.Core.Count < 1 || source.Core.EquipmentLockId != 0 || source.IsExpired(now) ||
            !ItemSlotBoundService.Allows(character.Inventory, source.Position, source.Core.Kind) ||
            Catalog.Get(creature.Core.ItemId) is not { } definition)
            return InventoryResult.Reject(123);
        InventoryItem fed;
        CreatureGrowthFood? growthFood = null;
        if (definition.UsesExperienceItems && definition.SelectiveGrowth is { } growth &&
            (request.ItemId == growth.ItemA || request.ItemId == growth.ItemB || request.ItemId == growth.ExperienceItem))
        {
            if (Catalog.GrowthFood(request.ItemId) is not { } food || detail.Level < food.MinimumLevel ||
                detail.Level >= definition.MaximumLevel || definition.SelectEvolutionLevel >= 0 && detail.Level >= definition.SelectEvolutionLevel)
                return InventoryResult.Reject(123);
            growthFood = food;
            fed = creature with { Creature = CreatureRules.GainItemExperience(detail, definition, Catalog, request.ItemId) };
            if (fed.Creature.Level > detail.Level && !definition.EvolutionQuest && growth.SelectLevel > 0 &&
                fed.Creature.Level >= growth.SelectLevel)
            {
                int targetSpecies = request.ItemId == growth.ExperienceItem ? growth.ExperienceSpecies :
                    fed.Creature.Mode1Field1 < 5 ? growth.SpeciesB : growth.SpeciesA;
                if (Catalog.Evolution(definition, targetSpecies) is not { } target) return InventoryResult.Reject(123);
                fed = Transform(fed, target);
            }
        }
        else
        {
            if (source.Core.Kind != ItemKind.CreatureConsumable || detail.Mode != 0 || definition.UsesExperienceItems || feed is null || detail.Level < feed.MinimumLevel)
                return InventoryResult.Reject(123);
            fed = creature with { Creature = CreatureRules.Feed(detail, feed, now) };
        }
        var consumed = InventoryDeleteService.Plan(character.Inventory, source.Position, request.ItemId, 1);
        if (!consumed.Accepted || consumed.Plan is null) return InventoryResult.Reject(123);
        var after = consumed.Plan.After;
        if (growthFood is { Materials.IsEmpty: false })
        {
            if (pieces is null) return InventoryResult.Reject(123);
            var materials = InventoryMaterialConsumptionService.Plan(after, growthFood.Materials, pieces, DateTimeOffset.FromUnixTimeSeconds(now));
            if (!materials.Accepted || materials.Plan is null) return InventoryResult.Reject(123);
            after = materials.Plan.After;
        }
        after = after with { Items = after.Items.Replace(creature, fed) };
        var plan = new InventoryPlan(character.Inventory, after, "creature_feed");
        long? deadline = plan.ChangedSlots.Select(plan.BeforeIndex.Find).OfType<InventoryItem>()
            .Where(item => item.ExpireTime > 0).Select(item => (long?)item.ExpireTime).Min();
        return new(true, 0, new(character.Inventory, after, "creature_feed", new(character, deadline)));
    }

    public InventoryResult Rename(Character character, InventorySlot cardSlot, string name, long now)
    {
        var creature = Equipped(character, now);
        var card = character.Inventory.Find(cardSlot);
        if (creature?.Creature is not { } detail || card is null || card.Uid <= 0 || card.Core.EquipmentLockId != 0 ||
            card.IsExpired(now) || card.Core.Count < 1 || card.Core.Kind != ItemKind.CreatureConsumable ||
            !ItemSlotBoundService.Allows(character.Inventory, card.Position, card.Core.Kind) ||
            !Catalog.RenameCard(card.Core.ItemId) || !CreatureRules.ValidName(name)) return InventoryResult.Reject(123);
        var consumed = InventoryDeleteService.Plan(character.Inventory, cardSlot, card.Core.ItemId, 1);
        if (!consumed.Accepted || consumed.Plan is null) return InventoryResult.Reject(123);
        var renamed = creature with { Creature = detail with { Name = name } };
        var after = consumed.Plan.After with { Items = consumed.Plan.After.Items.Replace(creature, renamed) };
        return new(true, 0, new(character.Inventory, after, "creature_rename", Deadline(character, creature, card)));
    }

    public InventoryResult Experience(Character character, int points, long now)
    {
        var creature = Equipped(character, now);
        if (creature?.Creature is not { } detail || Catalog.Get(creature.Core.ItemId) is not { } definition)
            return new(true, 0, new(character.Inventory, character.Inventory, "creature_experience", new(character)));
        float bonus = 0;
        long? deadline = creature.ExpireTime == 0 ? null : creature.ExpireTime;
        foreach (var artifact in character.Inventory.Items.Where(item => item.Position.List == InventoryListType.Equipment &&
            item.Position.Index is >= 27 and <= 29 && item.Core.Kind == ItemKind.CreatureEquipment && !item.IsExpired(now)))
            if ((definition.ArtifactSlots & (1 << (artifact.Position.Index - 27))) != 0)
            {
                float rate = Catalog.ExperienceRate(artifact.Core.ItemId, detail.Level);
                bonus += rate;
                if (rate != 0 && artifact.ExpireTime > 0) deadline = Math.Min(deadline ?? long.MaxValue, artifact.ExpireTime);
            }
        var updated = creature with { Creature = CreatureRules.GainExperience(detail, definition, Catalog, points, bonus) };
        if (updated.Creature.Level > detail.Level && !definition.EvolutionQuest && definition.EvolutionLevel > 0 &&
            updated.Creature.Level >= definition.EvolutionLevel && definition.EvolutionSpecies > 0 &&
            Catalog.Evolution(definition, definition.EvolutionSpecies) is { } evolution)
            updated = Transform(updated, evolution);
        return new(true, 0, new(character.Inventory, character.Inventory with { Items = character.Inventory.Items.Replace(creature, updated) },
            "creature_experience", new(character, deadline)));
    }

    public bool RecognizesFeed(Character character, int itemId, long now) => Catalog.Feed(itemId) is not null ||
        Equipped(character, now) is { } item && Catalog.Get(item.Core.ItemId)?.SelectiveGrowth is { } growth &&
            (itemId == growth.ItemA || itemId == growth.ItemB || itemId == growth.ExperienceItem);

    public InventoryResult Normalize(Character character, long now)
    {
        var item = Equipped(character, now);
        if (item?.Creature is not { Mode: 0 } detail || Catalog.Get(item.Core.ItemId) is not { UsesExperienceItems: true })
            return new(true, 0, new(character.Inventory, character.Inventory, "creature_normalize", new(character)));
        return Replace(character, item, item with { Creature = detail with { Mode = 1, Mode1Field0 = 10, Mode1Field1 = 5 } },
            "creature_normalize");
    }

    public int FoodConsumptionRate(Character character, long now)
    {
        long rate = 0;
        if (Equipped(character, now) is not { } item || Catalog.Get(item.Core.ItemId) is not { } definition) return 0;
        foreach (var artifact in character.Inventory.Items.Where(value => value.Position.List == InventoryListType.Equipment &&
            value.Position.Index is >= 27 and <= 29 && value.Core.Kind == ItemKind.CreatureEquipment && !value.IsExpired(now)))
            if ((definition.ArtifactSlots & (1 << (artifact.Position.Index - 27))) != 0)
                rate += Catalog.FoodConsumptionRate(artifact.Core.ItemId);
        return (int)Math.Clamp(rate, -100, int.MaxValue - 100L);
    }

    public static InventoryItem Transform(InventoryItem source, CreatureDefinition target)
    {
        var detail = source.Creature ?? throw new InvalidDataException("进化源缺少宠物详情");
        byte mode = target.UsesExperienceItems ? (byte)1 : (byte)0;
        if (detail.Mode != mode) detail = detail with { Mode = mode,
            Mode1Field0 = mode == 1 ? (byte)10 : (byte)0, Mode1Field1 = mode == 1 ? (byte)5 : (byte)0 };
        return source with { Core = source.Core with { ItemId = target.ItemId, SealFlag = 0 }, Creature = detail };
    }

    static InventoryResult Replace(Character character, InventoryItem before, InventoryItem after, string reason) =>
        new(true, 0, new(character.Inventory, character.Inventory with { Items = character.Inventory.Items.Replace(before, after) }, reason,
            new(character, before.ExpireTime == 0 ? null : before.ExpireTime)));

    static InventoryCharacterPrecondition Deadline(Character character, InventoryItem creature, InventoryItem source)
    {
        int deadline = creature.ExpireTime == 0 ? source.ExpireTime : source.ExpireTime == 0 ? creature.ExpireTime :
            Math.Min(creature.ExpireTime, source.ExpireTime);
        return new(character, deadline == 0 ? null : deadline);
    }
}
