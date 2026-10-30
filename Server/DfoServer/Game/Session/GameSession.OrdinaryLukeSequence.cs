using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;

namespace DfoServer.Game.Session;

internal sealed record OrdinaryLukeSequenceReceipt(long Generation, long Epoch, Character Character,
    SessionPhase Phase, int Day, OrdinaryLukeSequenceInfo Info);

internal sealed partial class GameSession
{
    public OrdinaryLukeSequenceReceipt? CaptureOrdinaryLukeSequence(uint sequenceId) => owner.Execute(() =>
    {
        if (sequenceId != OrdinaryLukeProgress.SequenceId || !Ready || Selected is not { } character ||
            Phase is SessionPhase.Unselected or SessionPhase.PvpRoom) return null;
        int day = QuestCycle.Day((timeProvider ?? TimeProvider.System).GetUtcNow());
        var progress = repository.LoadOrdinaryLukeProgress(owner, character.Id);
        return new OrdinaryLukeSequenceReceipt(owner.Generation, SelectionEpoch, character, Phase, day, progress.SequenceInfo(day));
    });

    public bool CanPublishOrdinaryLukeSequence(OrdinaryLukeSequenceReceipt receipt) =>
        owner.IsCurrent && Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.Epoch &&
        ReferenceEquals(Selected, receipt.Character) && Phase == receipt.Phase &&
        QuestCycle.Day((timeProvider ?? TimeProvider.System).GetUtcNow()) == receipt.Day;
}
