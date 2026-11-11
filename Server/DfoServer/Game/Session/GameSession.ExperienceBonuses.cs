using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    ExperienceBonuses PlanExperience(Character character, ExperienceEvent kind, uint basis, uint rank, long now, uint dungeonId, string equipmentModule = "dungeon")
    {
        ExperienceBonuses Complete(ExperienceBonuses reward) => DfoServer.Game.PcRoom.FatigueAccelerationRules.Apply(ApplyAdventurerExperience(character, kind, basis, now, reward), basis,
            (kind is ExperienceEvent.Kill or ExperienceEvent.Clear) && formalDungeon is { } dungeon && dungeon.Entry.DungeonId == dungeonId
                ? dungeon.Run.FatigueAccelerationPercent : 0);
        if (experienceBonuses is not null)
            return Complete(experienceBonuses.Plan(new(character, kind, dungeonId, now, PremiumPcRoom, equipmentModule), basis, rank, contracts?.Growth));
        // 无资源用例和显式未装配调用保留既有契约/秘药能力，不伪造装备或外部系统资格。
        var rates = new List<ExperienceBonusRate>();
        if (GrowthContractPolicy.Active(character, contracts?.Growth, now))
            rates.Add(new(ExperienceBonusSource.GrowthContract, contracts!.Growth!.ExperiencePercent));
        var result = ExperienceBonuses.Calculate(checked(basis + rank), basis, rates);
        uint potion = kind == ExperienceEvent.Quest ? 0 : ExpBonusPolicy.Bonus(character, checked(basis + rank), now);
        if (potion != 0) result = result with { Parts = result.Parts.Add(new(ExperienceBonusSource.Potion, potion)) };
        _ = result.Total;
        return Complete(result);
    }
}
