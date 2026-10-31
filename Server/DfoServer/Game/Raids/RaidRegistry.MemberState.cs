namespace DfoServer.Game.Raids;

internal sealed partial class RaidRegistry
{
    internal RaidChange? ChangeMemberState(RaidActor actor, bool inactive) => actor.Owner.Execute(() =>
    {
        lock (gate)
        {
            var before = FindCore(actor);
            if (!actor.IsCurrent || before is null) return null;
            int index = -1;
            for (int at = 0; at < before.Members.Length; at++)
                if (Same(before.Members[at].Actor, actor)) { index = at; break; }
            if (index < 0 || before.Members[index].Inactive == inactive) return null;
            var after = before with
            {
                Members = before.Members.SetItem(index, before.Members[index] with { Inactive = inactive }),
                Revision = ++revision
            };
            parties[before.Key] = after;
            return new RaidChange(before, after) { StateMember = actor.Character };
        }
    });
}
