using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.Game.Town;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal Func<TownTravelReceipt, Task<bool>>? FiendwarAreaTravelArrived { private get; set; }
    IFiendwarAreaTravelStore? FiendwarAreaTravelStore => repository as IFiendwarAreaTravelStore;

    // 等待真实发送时成员命令门禁已释放，合法后继快照使旧回执失效，不作为普通旅行不变量异常。
    internal bool CanPublishFiendwarAreaTravel(TownTravelReceipt receipt) => owner.IsCurrent &&
        Phase == SessionPhase.Town && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        ReferenceEquals(Selected, receipt.Character);

    internal FiendwarAreaTravelReservation? CaptureFiendwarAreaTravel(RaidActor actor, AreaRequest request) =>
        owner.Execute<FiendwarAreaTravelReservation?>(() =>
        {
            if (!actor.IsCurrent || !CanChangeTownParty || ChannelEntry?.Type != 45 ||
                raidCombat is not { Battle.Kind: RaidKind.Fiendwar } binding || binding.Actor != actor ||
                Selected is not { Position.Town: 57 } character || FiendwarAreaTravelArrived is not { } publish)
                return null;
            var plan = TownTravel.Plan(character, request, towns, completed, ChannelEntry, fiendwarActive: true);
            TravelReason = plan.Reason;
            return plan.Accepted ? new(actor, character, plan, owner.Generation, SelectionEpoch, publish) : null;
        });

    internal bool MatchesFiendwarAreaTravel(FiendwarAreaTravelReservation row) =>
        row.Actor.IsCurrent && ReferenceEquals(row.Actor.Session, this) && row.Actor.Owner == owner &&
        row.Generation == owner.Generation && row.Epoch == SelectionEpoch && CanChangeTownParty &&
        ReferenceEquals(Selected, row.Expected) && row.Plan.Accepted &&
        raidCombat is { Battle.Kind: RaidKind.Fiendwar } binding && binding.Actor == row.Actor;

    internal static FiendwarAreaTravelReceipt[] CommitFiendwarAreaTravel(FiendwarAreaTravelReservation[] rows,
        AntonRaidBattle.FiendwarAreaMovement movement, Func<bool> current) => AccountOwners.Lease.ExecuteGroup(
        rows.Select(row => (row.Actor.Owner, row.Actor.Character)).ToArray(), () =>
        {
            if (!current() || !movement.IsCurrent || rows.Any(row => !row.Actor.Session.MatchesFiendwarAreaTravel(row)))
                return [];
            if (rows[0].Actor.Session.FiendwarAreaTravelStore is not { } store) return [];
            var saved = store.CommitFiendwarAreaTravel(rows, () => current() && movement.IsCurrent);
            if (saved.Length == 0) return [];
            var receipts = new FiendwarAreaTravelReceipt[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                var session = row.Actor.Session;
                session.Selected = saved[i];
                session.npcTownVisit = null;
                session.InvalidateImageCommunication();
                session.InvalidateExpertJobStore();
                receipts[i] = new(row.Actor, new(saved[i], row.Generation, row.Epoch), row.Publish);
            }
            movement.Install();
            return receipts;
        });
}
