using DfoServer.GameWorld.Adventure;
using System.Collections.Immutable;

namespace DfoServer.Game.Adventure;

internal sealed class AdventureRepresentativeService(AdventureCatalog catalog)
{
    public AdventureCatalog Catalog => catalog;
    public bool Busy(AdventureSnapshot snapshot, byte job, byte grow)
    {
        ushort? id = snapshot.State.Representatives.SingleOrDefault(row => row.Job == job && row.Grow == grow).CharacterId ??
            snapshot.Representatives(catalog).FirstOrDefault(row => row.Job == job && row.ProfessionGrow == grow)?.Id;
        return id is { } member && snapshot.State.Expeditions.Any(run => run.Members.Contains(member));
    }
    public AdventureRepresentativePlan? Prepare(AdventureSnapshot snapshot, ushort actor, byte job, byte grow, ushort? id)
    {
        if (Busy(snapshot, job, grow) || !catalog.Professions.ContainsKey((job, grow)) || id is { } selected &&
            !snapshot.Members.Any(member => member.Id == selected && member.Job == job && member.ProfessionGrow == grow &&
                catalog.Eligible(member.Job, member.Grow, member.Level))) return null;
        var rows = snapshot.State.Representatives.Where(row => row.Job != job || row.Grow != grow)
            .Append(new AdventureSelection(job, grow, id)).OrderBy(row => row.Job).ThenBy(row => row.Grow).ToImmutableArray();
        return new(snapshot, rows, actor);
    }
}
