using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.TagEvildom;

internal static class TagEvildomLearning
{
    // 备用角色不执行选角写事务；先在owner锁外复用同一技能账本判定，不能投影已到期却尚未回退的技能。
    public static bool IsCurrent(Character character, SkillLearningCatalog catalog, long now)
    {
        if (!character.Learning.Initialized) return false;
        try
        {
            var before = character.Learning;
            var normalized = SkillPointLedger.Synchronize(character.Job, character.GrowType, character.Level,
                before, catalog, catalog.ActiveExpertContractLevels(before.ExpertContractExpiresAt, now));
            return before.Trees.Zip(normalized.Trees).All(pair => pair.First.Context == pair.Second.Context &&
                pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp &&
                pair.First.Skills.SequenceEqual(pair.Second.Skills) && pair.First.SameCombos(pair.Second));
        }
        catch (NotSupportedException) { return false; }
        catch (KeyNotFoundException) { return false; }
    }
}
