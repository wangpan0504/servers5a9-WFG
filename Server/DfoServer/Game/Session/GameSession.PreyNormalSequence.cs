using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;

namespace DfoServer.Game.Session;

internal sealed record PreyNormalSequenceReceipt(long Generation, long Epoch, Character Character,
    SessionPhase Phase, int Day, PreyNormalSequenceInfo Info);

internal sealed partial class GameSession
{
    public PreyNormalSequenceReceipt? CapturePreyNormalSequence() => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || Phase is SessionPhase.Unselected or SessionPhase.PvpRoom) return null;
        int day = QuestCycle.Day((timeProvider ?? TimeProvider.System).GetUtcNow());
        return new PreyNormalSequenceReceipt(owner.Generation, SelectionEpoch, character, Phase, day,
            repository.LoadPreyNormalProgress(owner, character.Id).Information(day));
    });

    public bool CanPublishPreyNormalSequence(PreyNormalSequenceReceipt receipt) => owner.IsCurrent && Ready &&
        owner.Generation == receipt.Generation && SelectionEpoch == receipt.Epoch && ReferenceEquals(Selected, receipt.Character) &&
        Phase == receipt.Phase && QuestCycle.Day((timeProvider ?? TimeProvider.System).GetUtcNow()) == receipt.Day;
}
