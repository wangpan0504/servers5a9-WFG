using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed record PreyBoardingState(RaidActor Actor, AntonRaidBattle Battle, byte Area, long Order);

internal sealed partial class GameSession
{
    internal AntonRaidBattleChange? CompletePreyBoardingPreparation(Guid instance) => owner.Execute(() =>
    {
        if (CapturePreyBoarding() is not { Area: 2 } boarding || boarding.Battle.Instance != instance ||
            boarding.Battle.View is not { State: AntonRaidBattleState.Fighting, PhaseExpired: false, ClockFaulted: false }) return null;
        return boarding.Battle.CompletePreyBoardingPreparation(boarding.Actor);
    });

    Guid preyBoardingInstance;
    long preyBoardingEpoch, preyBoardingOrder;
    byte preyBoardingArea;

    // 只在位置提交后的成队入口记录一次；回中间清除，仍留在侧区的单人保留原先到顺序。
    internal void RecordPreyBoardingArrival() => owner.Execute(() =>
    {
        var current = CapturePreyBoarding();
        if (current is not { Area: 4 or 5 })
        {
            preyBoardingInstance = Guid.Empty;
            preyBoardingArea = 0;
            preyBoardingOrder = 0;
        }
        else if (preyBoardingInstance != current.Battle.Instance || preyBoardingEpoch != SelectionEpoch ||
            preyBoardingArea != current.Area)
        {
            preyBoardingInstance = current.Battle.Instance;
            preyBoardingEpoch = SelectionEpoch;
            preyBoardingArea = current.Area;
            preyBoardingOrder = current.Battle.NextPreyBoardingOrder();
        }
        return true;
    });

    // 只读取已提交位置和当前攻坚身份；分队标记不参与实际队伍选择。
    internal PreyBoardingState? CapturePreyBoarding() => owner.Execute(() =>
    {
        if (!CanChangeTownParty || formalDungeon is not null || PartyDungeon is not null ||
            raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid ||
            ChannelEntry?.Type != 50 || Selected?.Position is not { Town: 59, Area: >= 2 and <= 5 } position ||
            raidRegistry?.FindCurrent(this, SelectionEpoch) is not { Type: 3, Guide: false, Started: { } started } ||
            started.InstanceId != raid.Battle.Instance) return null;
        if (position.Area is 4 or 5 && (!HasPreyTeamTownAccess ||
            raid.Battle.View.State != AntonRaidBattleState.Fighting)) return null;
        long order = preyBoardingInstance == raid.Battle.Instance && preyBoardingEpoch == SelectionEpoch &&
            preyBoardingArea == position.Area ? preyBoardingOrder : 0;
        return new PreyBoardingState(raid.Actor, raid.Battle, position.Area, order);
    });

    internal RaidActor[] CapturePreyBoardingMembers(Guid instance) => owner.Execute(() =>
        raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid && raid.Battle.Instance == instance &&
        raidRegistry?.FindCurrent(this, SelectionEpoch) is { Type: 3, Guide: false, Started: { } started } party && started.InstanceId == instance
            ? party.Members.Select(member => member.Actor).ToArray() : []);
}
