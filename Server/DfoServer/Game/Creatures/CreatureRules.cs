using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Creatures;

namespace DfoServer.Game.Creatures;

internal static class CreatureRules
{
    public static readonly InventorySlot EquippedSlot = new(InventoryListType.Equipment, 26);

    public static CreatureDetail AdvanceSatiety(CreatureDetail detail, long seconds, bool fighting, bool reviveInTown = false, int foodRate = 0)
    {
        if (seconds < 0 || detail.Satiety > 100 || detail.SatietyFraction is < 0 or >= 36000)
            throw new InvalidDataException("宠物饱食时钟输入无效");
        if (detail.Mode != 0) return detail;
        if (!fighting && reviveInTown && detail.Satiety == 0)
            detail = detail with { Satiety = 1, SatietyFraction = 0 };
        // 统一用1/36000点保存小数，同时容纳百分比宠物装备修正和休息恢复。
        long elapsed = Math.Min(seconds, 36000);
        return ShiftSatiety(detail, fighting ? -elapsed * 6 * Math.Max(0, 100L + foodRate) : elapsed * 100);
    }

    public static CreatureDetail ShiftSatiety(CreatureDetail detail, long units, bool revive = false)
    {
        if (detail.Mode != 0) return detail;
        long amount = Math.Clamp(detail.Satiety * 36000L + detail.SatietyFraction + Math.Clamp(units, -3600000, 3600000), 0, 3600000);
        if (revive && amount < 36000) amount = 36000;
        return detail with { Satiety = (byte)(amount / 36000), SatietyFraction = (int)(amount % 36000) };
    }

    public static CreatureDetail Feed(CreatureDetail detail, CreatureFeedDefinition feed, long now)
    {
        if (detail.Mode != 0 || detail.Level < feed.MinimumLevel || now < 0)
            throw new InvalidDataException("当前宠物不能使用该饲料");
        int satiety = Math.Min(100, detail.Satiety + feed.Satiety);
        return detail with
        {
            Satiety = checked((byte)satiety),
            SatietyFraction = satiety == 100 ? 0 : detail.SatietyFraction,
            FeedEffectExpiresAt = checked(now + feed.EffectSeconds),
            FeedSkillRecoveryRate = feed.SkillRecoveryRate
        };
    }

    public static CreatureDetail GainExperience(CreatureDetail detail, CreatureDefinition definition, CreatureCatalog catalog,
        int points, float artifactRate = 0)
    {
        if (points < 0 || !float.IsFinite(artifactRate)) throw new InvalidDataException("宠物经验来源或倍率无效");
        if (points == 0 || detail.Mode != 0 || definition.UsesExperienceItems || detail.Satiety == 0 ||
            detail.Level >= definition.MaximumLevel || definition.SelectEvolutionLevel >= 0 && detail.Level >= definition.SelectEvolutionLevel)
            return detail;
        long bonus = (long)Math.Clamp(Math.Truncate((double)points * artifactRate / 100), -points, int.MaxValue);
        int experience = checked((int)Math.Min(int.MaxValue, (long)detail.Experience + points + bonus));
        byte level = Math.Max(detail.Level, catalog.Level(experience, definition));
        return detail with { Experience = experience, Level = level };
    }

    public static CreatureDetail GainItemExperience(CreatureDetail detail, CreatureDefinition definition, CreatureCatalog catalog, int itemId)
    {
        if (!definition.UsesExperienceItems || definition.SelectiveGrowth is not { } growth ||
            itemId != growth.ItemA && itemId != growth.ItemB && itemId != growth.ExperienceItem || detail.Level >= definition.MaximumLevel ||
            definition.SelectEvolutionLevel >= 0 && detail.Level >= definition.SelectEvolutionLevel)
            throw new InvalidDataException("成长道具或宠物成长阶段不符");
        if (detail.Mode != 1) detail = detail with { Mode = 1, Mode1Field0 = 10, Mode1Field1 = 5 };
        int intimacy = detail.Mode1Field1;
        if (itemId == growth.ItemA) intimacy = Math.Min(10, intimacy + 1);
        if (itemId == growth.ItemB) intimacy = Math.Max(0, intimacy - 1);
        int amount = itemId == growth.ExperienceItem ? growth.ExperienceValue : 1;
        if (amount <= 0) throw new InvalidDataException("EXP成长道具缺少正经验增量");
        int experience = checked(detail.Experience + amount);
        return detail with { Experience = experience, Level = Math.Max(detail.Level, catalog.Level(experience, definition)),
            Mode1Field0 = 10, Mode1Field1 = checked((byte)intimacy) };
    }

    public static bool ValidName(string name)
    {
        if (name.Any(char.IsControl)) return false;
        try { return DfoServer.Game.Characters.CharacterName.Encoding.GetByteCount(name) < 20; }
        catch (System.Text.EncoderFallbackException) { return false; }
    }
}
