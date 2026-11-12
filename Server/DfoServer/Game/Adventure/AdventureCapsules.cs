using DfoServer.Game.Characters;
using DfoServer.GameWorld.Adventure;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Adventure;

internal sealed record AdventureCapsulePlan(AdventureSnapshot Before, AdventureState After, Character Expected, CharacterGrowthPlan Growth);
internal sealed record AdventureCapsuleCommit(AdventureSnapshot Snapshot, Character Character);
internal sealed record AdventureCapsuleResult(bool Accepted, ushort Error, AdventureCapsuleCommit? Commit = null)
{
    public long Generation { get; init; }
    public long Epoch { get; init; }
}
internal sealed class AdventureCapsuleService(AdventureCatalog catalog, CharacterGrowthCatalog growth)
{
    public AdventureCatalog Catalog => catalog;
    public (AdventureCapsulePlan? Plan, ushort Error) Claim(AdventureSnapshot snapshot, Character character)
    {
        if (character.DungeonState is null) return (null, 3);
        if (character.Level < catalog.Capsule.MinimumLevel) return (null, 14);
        if (character.Level > catalog.Capsule.MaximumLevel) return (null, 13);
        if (snapshot.State.CapsuleExperience < catalog.Capsule.GaugeExperience) return (null, 3);
        var result = CharacterGrowthPlan.AddExperience(character, catalog.Capsule.GrantedExperience, growth);
        if (!result.Success) return (null, 3);
        return (new(snapshot, snapshot.State with { CapsuleExperience = snapshot.State.CapsuleExperience - catalog.Capsule.GaugeExperience },
            character, result.Plan!), 0);
    }
}
