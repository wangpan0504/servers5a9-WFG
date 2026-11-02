using DfoServer.Game.Dungeons;
using DfoServer.Game.Strongway2019;

namespace DfoServer.Game.Session;

// 只引用本次正式入口，不另存倒计时或副本状态。
internal sealed record Strongway2019NormalTimeout(SinglePlayerDungeonRun Run, long SelectionEpoch,
    PartyDungeonInstance? PartyDungeon);

internal sealed partial class GameSession
{
    internal Func<Strongway2019NormalTimeout, Task>? Strongway2019NormalTimeoutArrived { get; set; }

    internal Strongway2019NormalTimeout? CaptureStrongway2019NormalTimeout()
    {
        try
        {
            return owner.Execute(() => Ready && Phase is SessionPhase.Loading or SessionPhase.Dungeon &&
                formalDungeon is { Entry.Strongway2019TimeLimitSeconds: > 0,
                    Strongway2019Admission.Mode: Strongway2019Mode.Simple or Strongway2019Mode.Hero } dungeon &&
                dungeon.Run.Phase == SinglePlayerDungeonPhase.TimedOut && CurrentStrongway2019Match is null
                    ? new Strongway2019NormalTimeout(dungeon.Run, SelectionEpoch, PartyDungeon) : null);
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }

    internal bool IsCurrentStrongway2019NormalTimeout(Strongway2019NormalTimeout expected)
    {
        var current = CaptureStrongway2019NormalTimeout();
        return current is not null && current.SelectionEpoch == expected.SelectionEpoch &&
            ReferenceEquals(current.Run, expected.Run) && ReferenceEquals(current.PartyDungeon, expected.PartyDungeon);
    }
}
