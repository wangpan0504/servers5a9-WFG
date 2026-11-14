using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Time;

namespace DfoServer.Game.Session;

internal sealed record PracticeDayReceipt(Character Character, long Generation, long Epoch, SessionPhase Phase);

internal sealed partial class GameSession
{
    public PracticeDayReceipt? AdvancePracticeDay(DateTimeOffset now) => owner.Execute<PracticeDayReceipt?>(() =>
    {
        if (!CanAdvanceServiceDay() || Selected is not { DungeonState: { } state } character) return null;
        int day = ServiceDay.Number(now, (timeProvider ?? TimeProvider.System).LocalTimeZone);
        if (day <= state.PracticeDay) return null;
        Selected = repository.ResetPracticeDay(owner, character, day);
        return new(Selected, owner.Generation, SelectionEpoch, Phase);
    });

    public bool CanPublishPracticeDay(PracticeDayReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.Epoch && Phase == receipt.Phase &&
        Selected is { DungeonState: { } state } current && current.Id == receipt.Character.Id &&
        state.PracticeDay == receipt.Character.DungeonState!.PracticeDay && state.PracticeUsed == receipt.Character.DungeonState.PracticeUsed &&
        current.Revision == receipt.Character.Revision);

    public bool CanPublishPracticeEntry(FormalDungeonReceipt receipt) => owner.Execute(() =>
        receipt.Practice is { } transition && CanPublishFormalDungeon(receipt) &&
        Selected is { DungeonState: { } state } current && current.Id == receipt.Character.Id &&
        state.PracticeDay == transition.Day && state.PracticeUsed == transition.AfterUsed &&
        current.Revision == receipt.Character.Revision);
}
