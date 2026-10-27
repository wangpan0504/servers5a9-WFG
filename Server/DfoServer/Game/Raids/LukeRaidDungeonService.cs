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
    public string? UnavailableReason => policy.Rejection(catalog);

    public PartyDungeonNotice[] Select(GameSession leader, int dungeon, PartySnapshot? party)
    {
        var raid = registry.FindCurrent(leader, leader.SelectionEpoch);
        if (party is null || !parties.IsCurrent(party) || raid is not { Type: 1, Started: { } start } || UnavailableReason is not null ||
            !party.Members.Any(member => member.Slot == party.Leader && ReferenceEquals(member.Actor.Session, leader))) return [];
        var anchor = raid.Members.SingleOrDefault(member => ReferenceEquals(member.Actor.Session, leader));
        if (anchor is null || anchor.Squad is not (1 or 2)) return [];
        var members = raid.Members.Where(member => member.Squad == anchor.Squad).OrderBy(member => member.Slot).ToArray();
        if (members.Length is < 1 or > 4 || members[0] != anchor || members.Length != party.Members.Length ||
            members.Any(member => !party.Members.Any(other => ReferenceEquals(member.Actor.Session, other.Actor.Session) &&
                ReferenceEquals(member.Actor.Owner, other.Actor.Owner) && member.Actor.Character == other.Actor.Character.Id))) return [];
        return AccountOwners.Lease.ExecuteGroup(members.Select(member => (member.Actor.Owner, member.Actor.Character)).ToArray(), () =>
        {
            if (!parties.IsCurrent(party) || members.Any(member => !MemberCurrent(member))) return [];
            var battle = leader.CurrentRaidBattle(anchor.Actor);
            if (battle is not { Kind: RaidKind.Luke } || battle.HasPendingTransition || battle.Instance != start.InstanceId) return [];
            var view = battle.View;
            if (view.State != AntonRaidBattleState.Fighting || policy.Maze(view.Phase, dungeon) is not { } maze ||
                !view.Dungeons.TryGetValue(dungeon, out var state) || state != RaidDungeonState.Open) return [];
            var selections = members.Select(member => new RaidDungeonSelection(new(start.InstanceId, member.Actor, view.Phase,
                member.Squad, catalog, () => MemberCurrent(member), id => Allows(battle, view.Phase, id), RaidKind.Luke)
                { LukePolicy = policy, Battle = battle, LukeObjects = objects }, dungeon, maze)).ToArray();
            if (selections.Any(selection => !ReferenceEquals(selection.Admission.Actor.Session.CurrentRaidBattle(selection.Admission.Actor), battle) ||
                !selection.Admission.Actor.Session.CanBeginLukeDungeonSelection(selection.Admission))) return [];
            return selections.Select(selection => new PartyDungeonNotice(PartyDungeonNoticeKind.RaidSelection,
                party.Members.Single(member => ReferenceEquals(member.Actor.Session, selection.Admission.Actor.Session)), party.Members,
                Selection: selection.Admission.Actor.Session.InstallLukeDungeonSelection(selection))).ToArray();
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
            return !battle.HasPendingTransition && current.State == AntonRaidBattleState.Fighting && current.Phase == phase && policy.Maze(phase, id) is not null &&
                current.Dungeons.TryGetValue(id, out var state) && state == RaidDungeonState.Open;
        }
    }

    public bool CancelSelection(GameSession leader, PartySnapshot? party)
    {
        // 调用方保留取得命令门禁时的原队伍；不能改用新快照清除另一次选择。
        if (party is null || !parties.IsCurrent(party) ||
            !party.Members.Any(member => member.Slot == party.Leader && ReferenceEquals(member.Actor.Session, leader))) return false;
        return AccountOwners.Lease.ExecuteGroup(party.Members.Select(member => (member.Actor.Owner, member.Actor.Character.Id)).ToArray(), () =>
        {
            if (!parties.IsCurrent(party)) return false;
            foreach (var member in party.Members) member.Actor.Session.ClearLukeRaidSelection();
            return true;
        });
    }
}
