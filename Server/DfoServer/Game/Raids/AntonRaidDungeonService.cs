using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 只授权原开战实例的首阶段黑雾；不装配机关、阶段推进或奖励能力。
internal sealed class AntonRaidDungeonService(RaidRegistry registry, PartyService parties,
    RaidDungeonCatalog catalog, bool enabled)
{
    public bool Enabled => enabled;
    public IReadOnlyList<int> MissingMaps => catalog.MissingMaps;
    public bool Contains(int dungeon) => catalog.DungeonPaths.ContainsKey(dungeon);

    public PartyDungeonNotice[] Select(GameSession leader, int dungeon, PartySnapshot? party, out string? rejection)
    {
        rejection = "anton-entry-waiting-client-resources";
        if (!enabled) return [];
        rejection = "anton-entry-squad-mismatch";
        var raid = registry.FindCurrent(leader, leader.SelectionEpoch);
        if (party is null || !parties.IsCurrent(party) || raid is not { Type: 0, Started: { } start } ||
            !party.Members.Any(member => member.Slot == party.Leader && ReferenceEquals(member.Actor.Session, leader))) return [];
        var anchor = raid.Members.SingleOrDefault(member => ReferenceEquals(member.Actor.Session, leader));
        if (anchor is null || anchor.Squad is < 1 or > 5) return [];
        var members = raid.Members.Where(member => member.Squad == anchor.Squad).OrderBy(member => member.Slot).ToArray();
        if (members.Length is < 1 or > 4 || members[0] != anchor || members.Length != party.Members.Length ||
            members.Any(member => !party.Members.Any(other => ReferenceEquals(member.Actor.Session, other.Actor.Session) &&
                ReferenceEquals(member.Actor.Owner, other.Actor.Owner) && member.Actor.Epoch == other.Actor.Epoch &&
                member.Actor.Character == other.Actor.Character.Id))) return [];
        string? reason = "anton-entry-instance-mismatch";
        var result = AccountOwners.Lease.ExecuteGroup(members.Select(member => (member.Actor.Owner, member.Actor.Character)).ToArray(), () =>
        {
            if (!parties.IsCurrent(party) || members.Any(member => !MemberCurrent(member))) return [];
            var battle = leader.CurrentRaidBattle(anchor.Actor);
            if (battle is not { Kind: RaidKind.Anton } || battle.Instance != start.InstanceId) return [];
            reason = "anton-entry-dungeon-not-open";
            if (!Allows(battle, dungeon)) return [];
            reason = "anton-entry-missing-maps";
            if (catalog.Find(dungeon) is not { } definition) return [];
            var mazes = definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0).ToArray();
            reason = "anton-entry-maze-unproven";
            if (mazes.Length != 1) return [];
            var selections = members.Select(member => new RaidDungeonSelection(new(start.InstanceId, member.Actor, 0,
                member.Squad, catalog, () => MemberCurrent(member), id => Allows(battle, id)) { Battle = battle },
                dungeon, mazes[0].SourceOrdinal)).ToArray();
            reason = "anton-entry-authorization-changed";
            if (selections.Any(selection => !selection.Admission.Actor.Session.CanBeginRaidDungeonSelection(selection.Admission))) return [];
            return selections.Select(selection => new PartyDungeonNotice(PartyDungeonNoticeKind.RaidSelection,
                party.Members.Single(member => ReferenceEquals(member.Actor.Session, selection.Admission.Actor.Session)), party.Members,
                Selection: selection.Admission.Actor.Session.InstallRaidDungeonSelection(selection))).ToArray();
        });
        rejection = result.Length == 0 ? reason : null;
        return result;

        bool MemberCurrent(RaidMember member)
        {
            var current = registry.Find(member.Actor);
            return member.Actor.IsCurrent && RaidAdmissionPlan.Rejection(member.Actor.Session.ChannelEntry, member.Actor.Session.Selected!) is null &&
                current is { Type: 0, Started: { } started } && current.Key == raid.Key && started.InstanceId == start.InstanceId &&
                current.Members.Any(other => other.Actor == member.Actor && other.Squad == member.Squad);
        }
    }

    static bool Allows(AntonRaidBattle battle, int dungeon)
    {
        var view = battle.View;
        return !battle.HasPendingTransition && view is { Phase: 0, State: AntonRaidBattleState.Fighting } &&
            dungeon == 210 && view.Dungeons.TryGetValue(dungeon, out var state) && state == RaidDungeonState.Open;
    }
}
