using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed record LukeChannelCardsCapture(long Epoch, ushort Character, LukeChannelCardSelection Selection);

internal sealed partial class GameSession
{
    internal LukeChannelCardsCapture? CaptureLukeChannelCards(LukeChannelCards cards) => owner.Execute(() =>
        ChannelEntry?.Type == 32 && Selected is { } character && raidCombat is null &&
        (Ready && Phase == SessionPhase.Town || !Ready && Phase == SessionPhase.Unselected)
            ? new LukeChannelCardsCapture(SelectionEpoch, character.Id, cards.Current) : null);

    internal bool CanPublishLukeChannelCards(LukeChannelCardsCapture capture, LukeChannelCards cards) =>
        owner.IsCurrent && owner.Execute(() => ChannelEntry?.Type == 32 &&
            Selected?.Id == capture.Character && SelectionEpoch == capture.Epoch && raidCombat is null &&
            (Ready && Phase == SessionPhase.Town || !Ready && Phase == SessionPhase.Unselected) && cards.CurrentDay == capture.Selection.Day);
}
