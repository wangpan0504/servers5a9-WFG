using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed record LukeFacilityReportReceipt(RaidActor Actor, Guid Battle, DungeonRunIdentity Identity,
    LukeFacilityReportResult Result);

internal sealed partial class GameSession
{
    internal bool AcceptsLukeFacilityReports => owner.Execute(() => raidCombat is { Actor.IsCurrent: true } raid && raid.Battle.AcceptsLukeFacilityReports);

    // 必须通过原PartyDungeonInstance.Execute协调全员owner；不能仅凭本人连接绕过加载/场景屏障。
    internal LukeFacilityReportReceipt? ReportLukeFacility(int symbol, uint value, byte operation) => owner.Execute(() =>
    {
        if (!Ready || !CanMutatePartyDungeon || raidCombat is not { Actor.IsCurrent: true } raid || formalDungeon is not { } dungeon ||
            dungeon.Entry.RaidEntry?.InstanceId != raid.Battle.Instance || !CanMoveWithParty()) return null;
        var snapshot = dungeon.Run.Snapshot;
        var result = raid.Battle.ConfirmLukeFacility(raid.Actor, snapshot, symbol, value, operation);
        return result.Outcome == LukeFacilityReportOutcome.Rejected ? null :
            new LukeFacilityReportReceipt(raid.Actor, raid.Battle.Instance, snapshot.Identity, result);
    });

    internal bool CanPublishLukeFacility(LukeFacilityReportReceipt receipt) => owner.Execute(() =>
        raidCombat is { } raid && raid.Actor == receipt.Actor && raid.Actor.IsCurrent && raid.Battle.Instance == receipt.Battle &&
        formalDungeon?.Run.Snapshot.Identity == receipt.Identity);
}
