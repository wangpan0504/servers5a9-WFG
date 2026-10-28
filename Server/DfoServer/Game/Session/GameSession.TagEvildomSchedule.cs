using DfoServer.Game.TagEvildom;
using DfoServer.Game.Town;

namespace DfoServer.Game.Session;

internal sealed record TagEvildomScheduledReturn(long Generation, long Epoch, Guid Instance, long At);

internal sealed partial class GameSession
{
    public TagEvildomScheduledReturn? CurrentTagEvildomReturn() => owner.Execute(() =>
        Ready && formalDungeon?.Run.TagEvildomAdmission is { } admission &&
        Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement
            ? new TagEvildomScheduledReturn(owner.Generation, SelectionEpoch, formalDungeon.Run.Snapshot.Identity.InstanceId,
                TagEvildomSchedule.ReturnAt(admission.AdmittedAt)) : null);

    public TownTravelReceipt? ReturnExpiredTagEvildom(TagEvildomScheduledReturn expected) => owner.Execute(() =>
        CurrentTagEvildomReturn() == expected && (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds() >= expected.At
            ? ReturnFromFormalDungeon() : null);
}
