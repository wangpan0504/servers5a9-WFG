using DfoServer.Game.Town;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public bool Move(PositionRequest request) => owner.Execute(() =>
    {
        if (Phase != SessionPhase.Town || Selected is null)
            return false;
        var target = TownTravel.Walk(Selected, request, towns, completed);
        if (target is null)
            return false;
        if (target != Selected.Position)
            Selected = repository.Save(owner, Selected, target, Selected.Return);
        return true;
    });

    public bool Travel(AreaRequest request) => Travel(request, out _);

    public bool Travel(AreaRequest request, out TownTravelReceipt? receipt)
    {
        receipt = owner.Execute(() => CommitTravel(Phase == SessionPhase.Town && Selected is not null
            ? TownTravel.Plan(Selected, request, towns, completed)
            : TownTravelPlan.Reject("not-ready")));
        return receipt is not null;
    }

    public bool LeaveRoom() => LeaveRoom(out _);

    public bool LeaveRoom(out TownTravelReceipt? receipt)
    {
        receipt = owner.Execute(() => CommitTravel(Phase == SessionPhase.Town && Selected is not null
            ? TownTravel.LeaveRoom(Selected, towns, completed)
            : TownTravelPlan.Reject("not-in-ready-room")));
        return receipt is not null;
    }

    TownTravelReceipt? CommitTravel(TownTravelPlan plan)
    {
        TravelReason = plan.Reason;
        if (!plan.Accepted)
            return null;
        Selected = repository.Save(owner, Selected!, plan.Position!, plan.Return);
        return new(Selected, owner.Generation, SelectionEpoch);
    }

    public bool CanPublishTravel(TownTravelReceipt receipt) => owner.Execute(() =>
    {
        if (!Ready || receipt.Generation != owner.Generation || !MatchesSelection(receipt.SelectionEpoch, receipt.Character.Id))
            return false;
        // 同次选角内不能静默丢掉已提交切图并继续收请求；让连接关闭后重读持久位置。
        if (!ReferenceEquals(Selected, receipt.Character))
            throw new InvalidOperationException("Town travel snapshot changed before publication");
        return true;
    });
}
