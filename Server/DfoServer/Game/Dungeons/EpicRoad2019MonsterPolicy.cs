namespace DfoServer.Game.Dungeons;

internal static class EpicRoad2019MonsterPolicy
{
    internal static bool Matches(DungeonEntryPlan entry) => entry.DungeonId is 400000097 or 400000098 &&
        entry.IsOrdinary && !entry.IsPractice && entry.HellParty is null && !entry.IsInfernoParty && !entry.IsCosmo;

    internal static bool CanPromote(DungeonPlannedMonster monster) => monster.SpawnType == 0 &&
        monster.AiCharacter is null && monster.HellWave is null && monster.CanReportDeath && monster.IsBlocking &&
        !monster.IsFriendly && !monster.IsBoss && !monster.IsDummyMapBoss &&
        monster.Source is { SpecialChild: null, NpcParameter: null, ScriptedRetirement: false, Modifiers.Count: 0 };

    internal static DungeonMonsterPlan Apply(DungeonMonsterPlan plan, DungeonEntryPlan entry) => !Matches(entry) ? plan :
        plan with { Monsters = plan.Monsters.Select(monster => CanPromote(monster)
            ? monster with { SpawnType = 1 } : monster).ToArray() };
}
