using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;

namespace DfoServer.Game.Session;

internal sealed record AntonNormalSequenceReceipt(long Generation, long Epoch, Character Character,
    SessionPhase Phase, int Day, AntonNormalSequenceInfo Info);

internal sealed partial class GameSession
{
    public AntonNormalSequenceReceipt? CaptureAntonNormalSequence() => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || Phase is SessionPhase.Unselected or SessionPhase.PvpRoom) return null;
        int day = QuestCycle.Day((timeProvider ?? TimeProvider.System).GetUtcNow());
        return new AntonNormalSequenceReceipt(owner.Generation, SelectionEpoch, character, Phase, day,
            repository.LoadAntonNormalProgress(owner, character.Id).Information(day));
    });

    public bool CanPublishAntonNormalSequence(AntonNormalSequenceReceipt receipt) => owner.IsCurrent && Ready &&
        owner.Generation == receipt.Generation && SelectionEpoch == receipt.Epoch && ReferenceEquals(Selected, receipt.Character) &&
        Phase == receipt.Phase && QuestCycle.Day((timeProvider ?? TimeProvider.System).GetUtcNow()) == receipt.Day;
}
