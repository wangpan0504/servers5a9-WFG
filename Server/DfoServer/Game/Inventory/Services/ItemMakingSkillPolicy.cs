using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed record ItemMakingSkillGain(byte Type, byte Status, ushort Amount);
internal static class ItemMakingSkillPolicy
{
    public static (ItemMakingSkillState State, ItemMakingSkillGain Gain) Gain(Character character,
        ItemRecipeDefinition recipe, ItemMakingSkillCatalog catalog, Func<long, long> random)
    {
        if (!catalog.Available) throw new InvalidDataException("缺少制作熟练度资源");
        byte type = recipe.MakingType ?? throw new InvalidDataException("配方没有制作熟练度类别");
        var before = character.Inventory.ItemMakingSkills;
        int points = before.Points[type], level = catalog.Level(points, character.Level);
        int amount = checked(catalog.ExperienceBase + (int)ItemMakingSkillCatalog.Draw(random, catalog.ExperienceRange));
        byte status = 0;
        int cap = character.Level >= CharacterGrowthCatalog.MaximumSupportedLevel ? catalog.Limits[^1].Points :
            catalog.Limits.Where(row => row.CharacterLevel > character.Level).Select(row => row.Points).DefaultIfEmpty(1000).First();
        if (level - recipe.MakingLevel >= catalog.RecipeLevelGap) { amount = 0; status = 13; }
        if (points >= cap) { amount = 0; status = 22; }
        if (points >= 1000) { amount = 0; status = 95; }
        amount = Math.Clamp(amount, 0, Math.Max(0, cap - points));
        return (before.Set(type, points + amount), new(type, status, checked((ushort)amount)));
    }
}
