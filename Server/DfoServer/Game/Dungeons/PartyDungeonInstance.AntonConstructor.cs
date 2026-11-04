using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Game.Raids;

namespace DfoServer.Game.Dungeons;

internal sealed partial class PartyDungeonInstance
{
    readonly AntonConstructorLedger antonConstructor = new();

    internal AntonObjectReport ReportAntonConstructorLast(GameSession reporter, AntonConstructorLast report) => Execute<AntonObjectReport>(current =>
    {
        if (reporter.CurrentFormalDungeonScene() is not { } scene || CaptureConstructor(reporter, scene, current) is not { } context)
            return new(false, false, 0);
        antonConstructor.Open(context.Scope, context.Definition);
        bool accepted = antonConstructor.Report(context.Scope, report);
        bool changed = ApplyConstructor(context);
        return new(accepted || changed, changed, 0);
    });

    void RecordAntonConstructorDeath(PartyMember[] current, List<PartyDungeonNotice> notices, ushort key)
    {
        var leader = current.SingleOrDefault(member => member.Slot == Leader);
        if (leader is null || notices.FirstOrDefault(notice => ReferenceEquals(notice.Recipient.Actor.Session, leader.Actor.Session) &&
                notice.Death is { Receipt: not null } observed && observed.MonsterKey == key)?.Death is not { Receipt: { } scene } result ||
            CaptureConstructor(leader.Actor.Session, scene, current) is not { } context ||
            leader.Actor.Session.RecordAntonConstructorDeath(result) is not { } death) return;
        antonConstructor.Open(context.Scope, context.Definition);
        if (!antonConstructor.Record(death)) return;
        var before = context.Battle.View;
        if (ApplyConstructor(context))
            notices.Add(new(PartyDungeonNoticeKind.RaidBattleSync, leader, current, scene,
                RaidBattleChange: new AntonRaidBattleChange(before, context.Battle.View)));
    }

    AntonConstructorContext? CaptureConstructor(GameSession source, FormalDungeonReceipt scene, PartyMember[] current) =>
        pendingDeath is null && pendingScene is null && loaded.Count == 0 &&
        current.Any(member => member.Slot == Leader && ReferenceEquals(member.Actor.Session, source)) &&
        current.All(member => member.Actor.Session.CanMoveWithParty() && member.Actor.Session.CurrentFormalDungeonScene() is { } own &&
            own.Snapshot.Identity.InstanceId == Id && own.Snapshot.CurrentMap == scene.Snapshot.CurrentMap) &&
        source.CaptureAntonConstructorContext(scene) is { } context && context.Scope.Run == Id ? context : null;

    bool ApplyConstructor(AntonConstructorContext context)
    {
        bool changed = false;
        antonConstructor.Apply(context.Scope, fact =>
        {
            bool accepted = context.Battle.ConfirmAntonConstructorEffect(context.Actor, context.Run, fact, out bool applied);
            changed |= applied;
            return accepted;
        });
        return changed;
    }
}
