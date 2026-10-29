using DfoServer.Game.Characters;
using DfoServer.Game.Social;
using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Pvp;
using System.Collections.Immutable;

namespace DfoServer.Game.Session;

// 普通练习没有任务接取身份，不能产生风云训练完成事实。
internal sealed record PvpPracticeRun(SocialActor Actor, Guid Instance, long Version)
{
    public ImmutableArray<PvpPracticeCombo> Combos { get; init; } = [];
    public PvpArcadeMode? Mode { get; init; }
    public ImmutableArray<ImmutableHashSet<int>> Rounds { get; init; } = [];
    public ImmutableHashSet<int> RandomRounds { get; init; } = [];
    public PvpArcadeRoundState Round { get; init; } = PvpArcadeRoundState.Initial;
}

internal sealed partial class GameSession
{
    PvpPracticeRun? pvpPractice;
    long pvpPracticeVersion;

    internal PvpPracticeRun? BeginPvpPractice(bool blocked, ImmutableArray<PvpPracticeCombo> combos = default,
        PvpArcadeRoundPlan? arcade = null, Character? expected = null) => owner.Execute(() =>
    {
        if (blocked || !Ready || Phase != SessionPhase.Town || Selected is not { Position.Town: 10, Position.Area: 0 or 4 or 5 } ||
            ChannelEntry is null || !ChannelAreaLanding.IsPvpType(ChannelEntry.Type) ||
            pvpPractice is not null || pvpTraining is not null || pvpArena is not null || pvpMatching is not null ||
            CurrentParty?.Invoke() is not null || pendingRental is not null || HasPendingTownQuestCommit ||
            HasActiveDungeonContinuation || HasPendingQuestReplay || HasPendingFormalCard) return null;
        if (expected is not null && !ReferenceEquals(expected, Selected)) return null;
        if (!combos.IsDefault && combos.Any(row => row.Job != Selected.Job || row.Grow != Selected.GrowType)) return null;
        pvpPractice = new(new(owner, this, Selected, SelectionEpoch), Guid.NewGuid(), ++pvpPracticeVersion)
        { Combos = combos.IsDefault ? [] : combos, Mode = arcade?.Mode, Rounds = arcade?.Allowed ?? [], RandomRounds = arcade?.RandomRounds ?? [] };
        SetState(SessionPhase.PvpRoom);
        return pvpPractice;
    });

    internal PvpPracticeRun? ReadPvpPractice() => owner.Execute(() => pvpPractice);

    internal bool ReportPvpPractice(PvpArcadeRoundReport report) => owner.Execute(() =>
    {
        if (pvpPractice is not { Mode: not null } run || !CanPublishPvpPractice(run) ||
            run.Round.Apply(report, run.Rounds, run.RandomRounds) is not { } next) return false;
        pvpPractice = run with { Round = next };
        return true;
    });

    internal PvpPracticeRun? EndPvpPractice(PvpPracticeRun expected) => owner.Execute(() =>
    {
        if (!CanPublishPvpPractice(expected)) return null;
        SetState(SessionPhase.Town);
        return expected with { Version = ++pvpPracticeVersion };
    });

    internal bool CanPublishPvpPractice(PvpPracticeRun run, bool removed = false) =>
        run.Actor.IsCurrent() && ReferenceEquals(run.Actor.Session, this) && run.Version == pvpPracticeVersion &&
        (removed ? Phase == SessionPhase.Town && pvpPractice is null :
            Phase == SessionPhase.PvpRoom && ReferenceEquals(pvpPractice, run));
}
