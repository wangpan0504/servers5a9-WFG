using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Creatures;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Creatures;

internal sealed class CreatureChangeService(Func<int, ItemTemplate> items, Func<int, CreatureChangeDefinition?> definitions,
    CreatureCatalog creatures, TimeProvider? clock = null, Func<long, long>? random = null,
    CreatureGroupCatalog? groups = null, Func<int, CreatureGrowthItemDefinition?>? growthItems = null,
    Func<int, ActionConsumableDefinition?>? actionItems = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    readonly Func<long, long> random = random ?? Random.Shared.NextInt64;

    public InventoryResult Plan(Character character, InventorySlot sourceSlot, InventorySlot targetSlot, bool inTown = true) =>
        Plan(character, sourceSlot, targetSlot, (source, _) => definitions(source.Core.ItemId), inTown, "creature_change", true);

    public InventoryResult PlanGroup(Character character, InventorySlot sourceSlot, InventorySlot targetSlot, uint choice, bool inTown) =>
        Plan(character, sourceSlot, targetSlot, (source, target) => growthItems?.Invoke(source.Core.ItemId) is { } item
            ? groups?.Resolve(item, target.Core.ItemId, choice) : null, inTown, "creature_group_convert", false);

    InventoryResult Plan(Character character, InventorySlot sourceSlot, InventorySlot targetSlot,
        Func<InventoryItem, InventoryItem, CreatureChangeDefinition?> resolve, bool inTown, string reason, bool consumeAction)
    {
        var time = clock.GetUtcNow();
        long now = time.ToUnixTimeSeconds();
        var before = character.Inventory;
        var source = before.Find(sourceSlot);
        var target = before.Find(targetSlot);
        if (sourceSlot.List != InventoryListType.Main || targetSlot.List != InventoryListType.Pet ||
            source is not { Uid: > 0, Core.Kind: ItemKind.Consumable } || source.Core.Count < 1 ||
            source.Core.EquipmentLockId != 0 || source.IsExpired(now) ||
            !ItemSlotBoundService.Allows(before, sourceSlot, source.Core.Kind) ||
            target is not { Uid: > 0, Avatar: null, Creature: not null, Core.Kind: ItemKind.Creature } ||
            target.Core.Value != target.Uid || target.Core.EquipmentLockId != 0 || target.Core.IsRental || target.IsExpired(now) ||
            !ItemSlotBoundService.Allows(before, targetSlot, target.Core.Kind)) return InventoryResult.Reject(0);

        CreatureChangeDefinition? definition;
        try
        {
            definition = resolve(source, target);
            if (definition is null || definition.ItemId != source.Core.ItemId ||
                definition.ConsumeCount <= 0 || source.Core.Count < definition.ConsumeCount ||
                character.Level < definition.MinimumLevel || character.Level > definition.MaximumLevel ||
                target.Creature.Level < definition.CreatureMinimumLevel ||
                definition.SourceItemId > 0 && definition.SourceItemId != target.Core.ItemId ||
                definition.AllowedItems.Count > 0 && !definition.AllowedItems.Contains(target.Core.ItemId))
                return InventoryResult.Reject(0);
            var book = items(source.Core.ItemId);
            if (book.UseForbidden ||
                book.RequiredSkill > 0 && !character.Skills.Any(skill => skill.Id == book.RequiredSkill && skill.Level > 0) ||
                !ValidCreature(target.Core.ItemId)) return InventoryResult.Reject(0);
            // 抽取前核对整个奖池，缺失候选不能被静默过滤并改变其他候选的概率。
            foreach (var candidate in definition.Candidates)
                if (!ValidCreature(candidate.ItemId)) return InventoryResult.Reject(0);
        }
        catch (Exception error) when (error is InvalidDataException or FileNotFoundException or KeyNotFoundException)
        {
            return InventoryResult.Reject(0);
        }
        InventoryResult consumed;
        ActionConsumableDefinition? use = null;
        if (consumeAction)
        {
            use = actionItems?.Invoke(source.Core.ItemId);
            if (use is null || use.Label != (definition.Random ? "[change creature random]" : "[change creature]"))
                return InventoryResult.Reject(0);
            consumed = InventoryActionConsumableService.Plan(character, sourceSlot, use, inTown ? "[village]" : "[dungeon]",
                clock, time, definition.ConsumeCount);
        }
        else consumed = InventoryDeleteService.Plan(before, sourceSlot, source.Core.ItemId, definition.ConsumeCount);
        if (!consumed.Accepted || consumed.Plan is null) return InventoryResult.Reject(0);
        int resultId = definition.Select(random(definition.TotalWeight));
        // 固定转换的-1期限用于永久兑换；其余转换沿用原期限，实例和成长均不重建。
        var changed = target with { Core = target.Core with { ItemId = resultId,
            ExpireTime = definition.MakePermanent ? 0 : target.Core.ExpireTime },
            Creature = definition.MakePermanent ? target.Creature with { ExpireTime = 0 } : target.Creature };
        var after = consumed.Plan.After with { Items = consumed.Plan.After.Items.Replace(target, changed) };
        var plan = new InventoryPlan(before, after, reason);
        long? deadline = plan.ChangedSlots.Select(plan.BeforeIndex.Find).OfType<InventoryItem>().Append(target)
            .Where(item => item.ExpireTime > 0).Select(item => (long?)item.ExpireTime).Min();
        if (use?.DailyLimit > 0) deadline = Math.Min(deadline ?? long.MaxValue, DailyBoosterUsage.NextReset(time).ToUnixTimeSeconds());
        return new(true, 0, new(before, after, reason, new(character, deadline, clock, requirePosition: use is not null)));

        bool ValidCreature(int id) => items(id) is { Kind: ItemKind.Creature, EquipmentSlot: 26, CreatureSubType: 0 } &&
            creatures.Get(id) is { Egg: false };
    }
}
