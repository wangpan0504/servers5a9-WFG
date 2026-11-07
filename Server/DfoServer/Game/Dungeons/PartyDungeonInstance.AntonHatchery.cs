using DfoServer.Game.Party;

namespace DfoServer.Game.Dungeons;

internal sealed partial class PartyDungeonInstance
{
    internal bool CanTransferAntonHatchery(PartySnapshot party) => IsExecuting && pendingDeath is null &&
        pendingScene is null && loaded.Count == 0 &&
        Members.Length == party.Members.Length && Members.All(member => party.Members.Contains(member) &&
            member.IsCurrent() && ReferenceEquals(member.Actor.Session.PartyDungeon, this));
}
