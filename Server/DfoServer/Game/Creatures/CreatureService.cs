using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Creatures;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Quests;
using System.Collections.Immutable;

namespace DfoServer.Game.Creatures;

internal sealed class CreatureService(CreatureCatalog catalog, TimeProvider? time = null, TownCatalog? towns = null, ItemCatalog? items = null)
{
    public CreatureCatalog Catalog { get; } = catalog;
    public TimeProvider Clock { get; } = time ?? TimeProvider.System;

    public InventoryResult UseEffect(Character character, InventorySlot position, bool inTown)
    {
        var source = character.Inventory.Find(position);
        var definition = source is null ? null : items?.ActionConsumable(source.Core.ItemId);
        if (definition is not { Label: "[creature effect]" }) return InventoryResult.Reject(19);
        string place = !inTown ? "[dungeon]" : towns?.IsRoom(character.Position, QuestEligibility.Completed(character)) == true
            ? "[seria room]" : "[village]";
        var paid = InventoryActionConsumableService.Plan(character, position, definition, place, Clock);
        if (!paid.Accepted) return paid;
        var states = paid.Plan!.After.ConsumableStates.Where(row => row.Kind != ConsumableStateKind.Effect ||
            row.ItemId == definition.Item.Id || items?.ActionConsumable(row.ItemId)?.Label != "[creature effect]").ToImmutableArray();
        return new(true, 0, new(character.Inventory, paid.Plan.After with { ConsumableStates = states },
            "creature_effect_item", paid.Plan.CharacterPrecondition));
    }

    public InventoryResult ExpireEffect(Character character)
    {
        long now = Clock.GetUtcNow().ToUnixTimeMilliseconds();
        var states = character.Inventory.ConsumableStates.Where(row => row.Kind != ConsumableStateKind.Effect || row.Active(now) ||
            items?.ActionConsumable(row.ItemId)?.Label != "[creature effect]").ToImmutableArray();
        // 客户端到期通知不含源或版本，迟到的旧通知不得撤销刚续用的新时限。
        return new(true, 0, new(character.Inventory, character.Inventory with { ConsumableStates = states }, "creature_effect_expire",
            new(character)));
    }

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
        if (creature?.Creature is not { } detail || source is null || request.Argument != 0 || request.OpaqueCheck > 32767 ||
            source.Uid <= 0 || source.Core.ItemId != request.ItemId || source.Core.Kind is not (ItemKind.CreatureConsumable or ItemKind.Consumable) ||
            source.Core.Count < 1 || source.Core.EquipmentLockId != 0 || source.IsExpired(now) ||
            !ItemSlotBoundService.Allows(character.Inventory, source.Position, source.Core.Kind) ||
            Catalog.Get(creature.Core.ItemId) is not { } definition)
            return InventoryResult.Reject(123);
        InventoryItem fed;
        if (Catalog.ExperienceItem(request.ItemId) is { } experienceItem)
        {
            if (source.Core.Kind != ItemKind.CreatureConsumable) return InventoryResult.Reject(123);
            var gained = CreatureRules.UseExperienceItem(detail, definition, Catalog, experienceItem);
            if (gained == detail) return InventoryResult.Reject(123);
            fed = EvolveAfterExperience(creature with { Creature = gained }, detail.Level, definition);
        }
        else if (definition.UsesExperienceItems && definition.SelectiveGrowth is { } growth &&
            (request.ItemId == growth.ItemA || request.ItemId == growth.ItemB || request.ItemId == growth.ExperienceItem))
        {
            if (Catalog.GrowthFood(request.ItemId) is not { } food || detail.Level < food.MinimumLevel ||
                detail.Level >= definition.MaximumLevel || definition.SelectEvolutionLevel >= 0 && detail.Level >= definition.SelectEvolutionLevel)
                return InventoryResult.Reject(123);
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
        if (name.Length == 0)
            name = CreatureRules.DefaultName(Catalog.Get(creature.Core.ItemId)?.Name ?? "");
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
        updated = EvolveAfterExperience(updated, detail.Level, definition);
        return new(true, 0, new(character.Inventory, character.Inventory with { Items = character.Inventory.Items.Replace(creature, updated) },
            "creature_experience", new(character, deadline)));
    }

    InventoryItem EvolveAfterExperience(InventoryItem updated, byte previousLevel, CreatureDefinition definition) =>
        updated.Creature!.Level > previousLevel && !definition.EvolutionQuest && definition.EvolutionLevel > 0 &&
            updated.Creature.Level >= definition.EvolutionLevel && definition.EvolutionSpecies > 0 &&
            Catalog.Evolution(definition, definition.EvolutionSpecies) is { } evolution ? Transform(updated, evolution) : updated;

    public bool RecognizesFeed(Character character, int itemId, long now) => Catalog.Feed(itemId) is not null ||
        Catalog.ExperienceItem(itemId) is not null ||
        Equipped(character, now) is { } item && Catalog.Get(item.Core.ItemId)?.SelectiveGrowth is { } growth &&
            (itemId == growth.ItemA || itemId == growth.ItemB || itemId == growth.ExperienceItem);

    public InventoryResult Normalize(Character character, long now)
    {
        var item = Equipped(character, now);
        if (item?.Creature is not { } detail || Catalog.Get(item.Core.ItemId) is not { } definition)
            return new(true, 0, new(character.Inventory, character.Inventory, "creature_normalize", new(character)));
        var updated = detail;
        if (detail.Name.Length == 0 && definition.Name.Length > 0)
            updated = updated with { Name = CreatureRules.DefaultName(definition.Name) };
        if (detail.Mode == 0 && definition.UsesExperienceItems)
            updated = updated with { Mode = 1, Mode1Field0 = 10, Mode1Field1 = 5 };
        return Replace(character, item, item with { Creature = updated },
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

    public InventoryResult UseEvolutionItem(Character character, InventorySlot sourceSlot, long now)
    {
        var creature = Equipped(character, now);
        var source = character.Inventory.Find(sourceSlot);
        if (creature?.Creature is not { } detail || source is null || source.Uid <= 0 ||
            source.Core.Count < 1 || source.Core.EquipmentLockId != 0 || source.IsExpired(now) ||
            sourceSlot.List is not (InventoryListType.Main or InventoryListType.Pet) ||
            !ItemSlotBoundService.Allows(character.Inventory, source.Position, source.Core.Kind) ||
            Catalog.Get(creature.Core.ItemId) is not { } definition)
            return InventoryResult.Reject(123);

        int targetSpecies = definition.SelectiveGrowth is { } growth &&
            (source.Core.ItemId == growth.ItemA || source.Core.ItemId == growth.ItemB || source.Core.ItemId == growth.ExperienceItem)
            ? (source.Core.ItemId == growth.ItemA ? growth.SpeciesA :
                source.Core.ItemId == growth.ItemB ? growth.SpeciesB : growth.ExperienceSpecies)
            : definition.EvolutionSpecies;
        if (targetSpecies <= 0 || Catalog.Evolution(definition, targetSpecies) is not { } target ||
            definition.EvolutionLevel > 0 && detail.Level < definition.EvolutionLevel)
            return InventoryResult.Reject(123);

        var consumed = InventoryDeleteService.Plan(character.Inventory, source.Position, source.Core.ItemId, 1);
        if (!consumed.Accepted || consumed.Plan is null)
            return InventoryResult.Reject(123);
        var evolved = Transform(creature, target);
        var after = consumed.Plan.After with { Items = consumed.Plan.After.Items.Replace(creature, evolved) };
        return new(true, 0, new(character.Inventory, after, "creature_evolution_item", Deadline(character, creature, source)));
    }

    public InventoryResult FillPoint(Character character, int points, long now)
    {
        if (points <= 0 || Equipped(character, now)?.Creature is not { } detail)
            return InventoryResult.Reject(123);
        long units = (long)Math.Clamp(points, 1, 100) * 36000L;
        return ShiftSatiety(character, units, detail.Satiety == 0, now);
    }

    public InventoryResult UseExpBook(Character character, InventorySlot sourceSlot, long now)
    {
        var source = character.Inventory.Find(sourceSlot);
        if (source is null || Catalog.ExperienceItem(source.Core.ItemId) is null)
            return InventoryResult.Reject(123);
        return Feed(character, new(sourceSlot, 0, source.Core.ItemId, 0), now);
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
