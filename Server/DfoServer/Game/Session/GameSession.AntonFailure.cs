using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 失败展示只接受原阶段已提交的不合格回执；待SQL重试的候选不能进入网络退场链。
    internal RaidResultDisplay? AntonFailureResult(RaidActor actor, Guid instance, int phase) => owner.Execute(() =>
        Ready && MatchesRaidCombatActor(actor) && raidCombat is { Actor: var bound, Battle: { Kind: RaidKind.Anton, HasAntonRewards: true } battle } &&
        bound == actor && battle.Instance == instance && battle.View is { PhaseExpired: true, ClockFaulted: false } view &&
        view.Phase == phase && battle.AntonEligibility(actor.Character, phase) is
            { Eligible: false, CountsAsClear: false, Reason: "phase-timeout", Source: AntonResourceRewards.Source } &&
        battle.AntonResult(phase) is { Result: 1, RawFlag: 0 } result ? result : null);
}
