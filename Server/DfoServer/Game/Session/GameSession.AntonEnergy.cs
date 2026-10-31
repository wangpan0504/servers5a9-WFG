using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal bool ReportAntonEnergy() => owner.Execute(() =>
        Ready && CanMutatePartyDungeon && raidCombat is { Actor.IsCurrent: true } raid && formalDungeon is { } dungeon &&
        dungeon.Entry.RaidEntry?.InstanceId == raid.Battle.Instance && CanMoveWithParty() &&
        raid.Battle.ConfirmAntonEnergy(raid.Actor, dungeon.Run.Snapshot));
}
