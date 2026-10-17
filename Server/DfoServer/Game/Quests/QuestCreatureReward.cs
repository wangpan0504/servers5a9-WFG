using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestCreatureReward
{
    public static bool Evolution(StoryQuest quest) => quest.RewardType == "[creature evolution]" &&
        quest.Rewards is [{ Type: 0, Value: > 0 and <= ushort.MaxValue, Text: null }] &&
        quest.Definition is { CreatureKind: > 0, CreatureLevel: >= 0 };

    public static bool Matches(Character character, StoryQuest quest, QuestCatalog catalog, long now)
    {
        if (quest.Definition is not { CreatureKind: > 0, CreatureLevel: >= 0 } required || catalog.Creatures is not { } creatures)
            return false;
        var item = character.Inventory.Find(CreatureRules.EquippedSlot);
        if (item is not { Uid: > 0, Core.Kind: GameWorld.Items.ItemKind.Creature, Creature: not null } ||
            item.Core.Value != item.Uid || item.IsExpired(now) || item.Creature.Level < required.CreatureLevel ||
            creatures.Get(item.Core.ItemId) is not { Egg: false } source || source.Species != required.CreatureKind)
            return false;
        return quest.RewardType != "[creature evolution]" || Evolution(quest) && CanEvolve(source, quest.Rewards[0].Value) &&
            creatures.Evolution(source, quest.Rewards[0].Value) is not null;
    }

    public static InventoryPlan Evolve(Character character, StoryQuest quest, QuestCatalog catalog, long now)
    {
        if (!Evolution(quest) || !Matches(character, quest, catalog, now))
            throw new InvalidDataException("宠物进化需要当前装备的对应种类、等级和精确进化产物");
        var source = character.Inventory.Find(CreatureRules.EquippedSlot)!;
        var definition = catalog.Creatures!.Get(source.Core.ItemId)!;
        int species = quest.Rewards[0].Value;
        if (!CanEvolve(definition, species))
            throw new InvalidDataException("任务奖励不是当前宠物声明的进化分支");
        var target = catalog.Creatures.Evolution(definition, species)!;
        var evolved = CreatureService.Transform(source, target);
        return new(character.Inventory, character.Inventory with { Items = character.Inventory.Items.Replace(source, evolved) },
            "quest_creature_evolution", new(character, source.ExpireTime == 0 ? null : source.ExpireTime));
    }

    static bool CanEvolve(GameWorld.Creatures.CreatureDefinition source, int species) => source.EvolutionSpecies == species ||
        source.EvolutionChoices.Contains(species) || source.SelectiveGrowth is { } growth &&
            (growth.SpeciesA == species || growth.SpeciesB == species);
}
