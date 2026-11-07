using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed partial class SinglePlayerDungeonRun
{
    // 只跨助手轮换继承至多一名辅助和一名三四仔；怪物、奖励及旧死亡事实不随行。
    readonly Dictionary<ushort, DungeonPlannedMonster> carriedCompanions = [];
    readonly HashSet<ushort> retiredCarriedCompanions = [];
    bool companionCarryBound;

    internal ImmutableArray<DungeonPlannedMonster> LivingPublishedCompanions() =>
        [.. allRooms.Where(room => room.Loaded).SelectMany(room => room.Plan.Monsters.Where(actor =>
                !room.Dead.Contains(actor.ObjectKey) && !room.Culled.Contains(actor.ObjectKey)))
            .Concat(carriedCompanions.Values.Where(actor => !retiredCarriedCompanions.Contains(actor.ObjectKey)))
            .Where(IsDedicatedCompanion).OrderBy(actor => actor.ObjectKey)];

    internal bool TryCarryCompanions(ImmutableArray<DungeonPlannedMonster> actors, uint firstAllocatedKey)
    {
        if (companionCarryBound || Phase != SinglePlayerDungeonPhase.Loading || entry.HelperRotation is null ||
            actors.IsDefault || actors.Length > 2 || firstAllocatedKey is < 1 or > 65536 ||
            actors.Any(actor => !IsDedicatedCompanion(actor) || actor.ObjectKey == 0 ||
                actor.ObjectKey == entry.CharacterId || actor.ObjectKey >= firstAllocatedKey) ||
            actors.Select(actor => actor.ObjectKey).Distinct().Count() != actors.Length ||
            actors.Select(actor => actor.AiCharacter!.Id).Distinct().Count() != actors.Length ||
            allRooms.Any(room => room.Plan.Monsters.Any(actor => actors.Any(old => old.ObjectKey == actor.ObjectKey)) ||
                room.Plan.SpecialObjects.Any(actor => actors.Any(old => old.ObjectKey == actor.ObjectKey)) ||
                room.Plan.HellWaves.Any(actor => actors.Any(old => old.ObjectKey == actor.ObjectKey)))) return false;
        foreach (var actor in actors) carriedCompanions.Add(actor.ObjectKey, actor);
        companionCarryBound = true;
        return true;
    }

    static bool IsDedicatedCompanion(DungeonPlannedMonster actor) => actor is
        { IsFriendly: true, IsBlocking: false, CanReportDeath: false, AiCharacter: { Faction: "[character]" } source } &&
        SupportCompanionResources.IsSupportedBase(source.Id) && !SupportCompanionResources.IsBlocked(actor.MonsterId);
}
