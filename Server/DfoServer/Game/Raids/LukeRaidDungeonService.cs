using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 正常666绑定的原Battle是唯一授权来源；此服务不创建攻坚实例，不收开团票，不保存第二份队伍。
internal sealed class LukeRaidDungeonService(RaidRegistry registry, PartyService parties,
    RaidDungeonCatalog catalog, LukeRaidRunPolicy policy, LukeRaidObjectCatalog? objects = null)
{
    public string? UnavailableReason => policy.Rejection(catalog) ??
        policy.EnergyRejection(catalog, objects) ??
        (policy.CardRoute is { } cards && !cards.MatchesObjects(objects) ? "luke-card-route-object-catalog-changed" : null) ??
        (policy.FixedRoute is { } route && !route.MatchesObjects(objects) ? "luke-fixed-route-object-catalog-changed" : null);

    public PartyDungeonNotice[] Select(GameSession leader, int dungeon, PartySnapshot? party) => SelectCore(leader, dungeon, party);

    public PartyDungeonNotice[] OpenSelection(GameSession leader, PartySnapshot? party) => SelectCore(leader, null, party);

    PartyDungeonNotice[] SelectCore(GameSession leader, int? dungeon, PartySnapshot? party)
    {
        var raid = registry.FindCurrent(leader, leader.SelectionEpoch);
        if (party is null || !parties.IsCurrent(party) || raid is not { Type: 1, Started: { } start } || UnavailableReason is not null ||
            !party.Members.Any(member => member.Slot == party.Leader && ReferenceEquals(member.Actor.Session, leader))) return [];
        var anchor = raid.Members.SingleOrDefault(member => ReferenceEquals(member.Actor.Session, leader));
        if (anchor is null || anchor.Squad is not (1 or 2)) return [];
        var members = raid.Members.Where(member => member.Squad == anchor.Squad).OrderBy(member => member.Slot).ToArray();
        if (members.Length is < 1 or > 4 || members.Length != party.Members.Length ||
            members.Any(member => !party.Members.Any(other => ReferenceEquals(member.Actor.Session, other.Actor.Session) &&
                ReferenceEquals(member.Actor.Owner, other.Actor.Owner) && member.Actor.Character == other.Actor.Character.Id))) return [];
        return AccountOwners.Lease.ExecuteGroup(members.Select(member => (member.Actor.Owner, member.Actor.Character)).ToArray(), () =>
        {
            if (!parties.IsCurrent(party) || members.Any(member => !MemberCurrent(member))) return [];
            var battle = leader.CurrentRaidBattle(anchor.Actor);
            if (battle is not { Kind: RaidKind.Luke } || battle.HasPendingTransition || battle.Instance != start.InstanceId) return [];
            var view = battle.View;
            if (!battle.LukePhaseOpen(view.Phase)) return [];
            var route = dungeon is { } target ? battle.CaptureLukeRoute(view.Phase, target, policy.FixedRoute, policy.CardRoute) : null;
            int? selectedMaze = dungeon is { } mazeDungeon ? battle.UsesLukeRoute(view.Phase) ? route?.MazeOrdinal : policy.Maze(view.Phase, mazeDungeon) : null;
            if (dungeon is { } chosen && (battle.UsesLukeRoute(view.Phase) !=
                (view.Phase == 0 && (policy.FixedRoute is not null || policy.CardRoute is not null)) || selectedMaze is null ||
                !view.Dungeons.TryGetValue(chosen, out var state) || state != RaidDungeonState.Open ||
                members.Any(member => !battle.AllowsLukeMemberRoute(member.Actor, view.Phase, chosen)))) return [];
            var admissions = members.Select(member => new RaidDungeonAdmission(start.InstanceId, member.Actor, view.Phase,
                member.Squad, catalog, () => MemberCurrent(member), id => Allows(battle, view.Phase, id) &&
                    battle.AllowsLukeMemberRoute(member.Actor, view.Phase, id), RaidKind.Luke)
                { LukePolicy = policy, Battle = battle, LukeObjects = objects }).ToArray();
            if (admissions.Any(admission => !ReferenceEquals(admission.Actor.Session.CurrentRaidBattle(admission.Actor), battle) ||
                !admission.Actor.Session.CanBeginRaidDungeonSelection(admission))) return [];
            // 空15只建立全分队的原生选图上下文，不预选地图或预留Run；直入同样必须先建立scene2。
            var fromTown = admissions.Select(admission => admission.Actor.Session.Phase == SessionPhase.Town).ToArray();
            return admissions.Select((admission, index) => new PartyDungeonNotice(PartyDungeonNoticeKind.RaidSelection,
                party.Members.Single(member => ReferenceEquals(member.Actor.Session, admission.Actor.Session)), party.Members,
                Selection: dungeon is { } selected ? admission.Actor.Session.InstallRaidDungeonSelection(
                    new(admission, selected, selectedMaze!.Value) { Route = route }) : admission.Actor.Session.InstallRaidDungeonMenu(admission),
                SceneTransition: fromTown[index])).ToArray();
        });

        bool MemberCurrent(RaidMember member)
        {
            var current = registry.Find(member.Actor);
            return member.Actor.IsCurrent && RaidAdmissionPlan.Rejection(member.Actor.Session.ChannelEntry, member.Actor.Session.Selected!) is null &&
                current is { Type: 1, Started: { } started } && current.Key == raid.Key && started.InstanceId == start.InstanceId &&
                current.Members.Any(other => other.Actor == member.Actor && other.Squad == member.Squad);
        }
        bool Allows(AntonRaidBattle battle, int phase, int id)
        {
            var current = battle.View;
            return battle.LukePhaseOpen(phase) && current.Phase == phase && (battle.UsesLukeRoute(phase) ? battle.CaptureLukeRoute(phase, id, policy.FixedRoute, policy.CardRoute) is not null : policy.Maze(phase, id) is not null) &&
                current.Dungeons.TryGetValue(id, out var state) && state == RaidDungeonState.Open;
        }
    }

    public bool CancelSelection(GameSession leader, PartySnapshot? party, bool preserveScene = false)
    {
        // 调用方保留取得命令门禁时的原队伍；不能改用新快照清除另一次选择。
        if (party is null || !parties.IsCurrent(party) ||
            !party.Members.Any(member => member.Slot == party.Leader && ReferenceEquals(member.Actor.Session, leader))) return false;
        return AccountOwners.Lease.ExecuteGroup(party.Members.Select(member => (member.Actor.Owner, member.Actor.Character.Id)).ToArray(), () =>
        {
            if (!parties.IsCurrent(party)) return false;
            foreach (var member in party.Members) member.Actor.Session.ClearLukeRaidSelection(preserveScene);
            return true;
        });
    }
}
