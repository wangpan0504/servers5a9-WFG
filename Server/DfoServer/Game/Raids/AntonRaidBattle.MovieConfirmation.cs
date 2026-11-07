using System.Collections.Immutable;

namespace DfoServer.Game.Raids;

internal sealed record RaidMovieConfirmation(AntonRaidBattleView View, bool Changed, bool Acknowledged);
internal sealed record LukeMovieCardSnapshot(AntonRaidBattleView View, Guid Event, RaidPhaseRewardPolicy Policy,
    ImmutableArray<LukeRewardResult> Team);

internal sealed partial class AntonRaidBattle
{
    // P2个人确认绑定原actor和阶段事件；同阶段重返只改变view，不能冒充新连接的真实确认。
    // 每个阶段及角色仅保存一条，容量受原团本人数和阶段数约束。
    readonly Dictionary<(int Phase, ushort Character), (RaidActor Actor, Guid Event)> lukePersonalMovies = [];

    internal LukeMovieCardSnapshot? CaptureLukeMovieCards(RaidActor actor, int phase, Guid? movieEvent = null)
    {
        lock (gate)
        {
            if (MovieEventLocked(phase) is not { } actual || movieEvent is { } expected && expected != actual ||
                !LukeMovieCardsReadyLocked(actor, phase, actual) || RewardPolicy(actor, phase) is not { } policy) return null;
            return new(current, actual, policy, LukeTeamEntitlements(current));
        }
    }

    internal bool LukeMovieCardsReady(RaidActor actor, int phase, Guid movieEvent)
    { lock (gate) return LukeMovieCardsReadyLocked(actor, phase, movieEvent); }

    bool LukeMovieCardsReadyLocked(RaidActor actor, int phase, Guid movieEvent) => current.Phase == phase &&
        MovieEventLocked(phase) == movieEvent && CanConfirmLukeMovie(actor, current) &&
        LukeEligibility(actor.Character, phase) is { Eligible: true } &&
        (phase == 0 ? LukeMovieBarrierReadyLocked() :
            lukePersonalMovies.TryGetValue((phase, actor.Character), out var confirmed) && confirmed == (actor, movieEvent));

    bool CompleteLukePersonalMovieLocked(RaidActor actor, int phase, Guid movieEvent)
    {
        var key = (phase, actor.Character);
        if (lukePersonalMovies.TryGetValue(key, out var previous) && previous == (actor, movieEvent)) return false;
        lukePersonalMovies[key] = (actor, movieEvent);
        return true;
    }
    // 电影身份沿阶段已封存的完成事件；成员退场可以改变显示版本，不能替换这个事件。
    internal (AntonRaidBattleView View, Guid Event)? CaptureMovie(RaidActor actor)
    {
        lock (gate) return CanConfirmMovieLocked(actor, current) &&
            RewardPolicy(actor, current.Phase) is { PublishResult: true } && MovieEventLocked(current.Phase) is { } movieEvent
                ? (current, movieEvent) : null;
    }

    Guid? MovieEventLocked(int phase) => HasAntonRewards
        ? antonPhaseCommits.ContainsKey(phase) && antonSettlements.TryGetValue(phase, out var anton) ? anton.Event : null
        : lukePhaseCommits.ContainsKey(phase) && lukeSettlements.TryGetValue(phase, out var luke) ? luke.Event : null;

    bool CanConfirmMovieLocked(RaidActor actor, AntonRaidBattleView view) => HasAntonRewards
        ? CanConfirmAntonMovie(actor, view) : Kind == RaidKind.Fiendwar ? CanConfirmFiendwarMovie(actor, view)
        : Kind == RaidKind.Prey ? CanConfirmPreyMovie(actor, view) : CanConfirmLukeMovie(actor, view);

    // 调用方持本人owner；锁内只提交电影事实及ACK入队，不发布、不等待网络或SQL。
    internal RaidMovieConfirmation? ConfirmMovie(RaidActor actor, AntonRaidBattleView expected, Guid movieEvent,
        bool completed, Func<bool> acknowledge)
    {
        lock (gate)
        {
            var view = current;
            if (expected.Instance != Instance || expected.Phase != view.Phase ||
                expected.State != AntonRaidBattleState.Reward || movieEvent == Guid.Empty ||
                MovieEventLocked(view.Phase) != movieEvent || !CanConfirmMovieLocked(actor, view) ||
                RewardPolicy(actor, view.Phase) is not { PublishResult: true }) return null;
            bool changed = completed && (HasAntonRewards ? CompleteAntonMovie(actor, view)
                : Kind == RaidKind.Fiendwar ? CompleteFiendwarMovie(actor, view)
                : Kind == RaidKind.Prey ? CompletePreyMovie(actor, view)
                : view.Phase == 0 || LukeEligibility(actor.Character, view.Phase) is { Eligible: false }
                    ? CompleteLukeFirstMovie(actor, view) : CompleteLukePersonalMovieLocked(actor, view.Phase, movieEvent));
            return new(view, changed, acknowledge());
        }
    }
}
