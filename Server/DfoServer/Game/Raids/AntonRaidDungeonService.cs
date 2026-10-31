using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 正式进图只读原 Battle 当前开放图。阶段迁移由 Battle.PhaseProgress 决定，本服务不解释计时或机关。
internal sealed class AntonRaidDungeonService(RaidRegistry registry, PartyService parties,
    RaidDungeonCatalog catalog, bool enabled, RaidKind kind = RaidKind.Anton)
{
    public bool Enabled => enabled;
    public IReadOnlyList<int> MissingMaps => catalog.MissingMaps;
    public bool Contains(int dungeon) => catalog.DungeonPaths.ContainsKey(dungeon);

    public PartyDungeonNotice[] Select(GameSession leader, int dungeon, PartySnapshot? party, out string? rejection,
        bool replayLoading = false)
    {
        rejection = "anton-entry-waiting-client-resources";
        if (!enabled) return [];
        rejection = "anton-entry-squad-mismatch";
        var raid = registry.FindCurrent(leader, leader.SelectionEpoch);
        if (party is null || !parties.IsCurrent(party) || raid is not { Started: { } start } || raid.Type != (byte)kind ||
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
            if (battle is null || battle.Kind != kind || battle.Instance != start.InstanceId) return [];
            // Reward后读取原聚合的实际phase；固定0会使已开放218/219被旧阶段授权拒绝。
            int phase = battle.View.Phase;
            reason = "anton-entry-dungeon-not-open";
            if (!Allows(battle, dungeon)) return [];
            if (replayLoading)
            {
                reason = "anton-entry-loading-identity-changed";
                var instance = leader.PartyDungeon;
                if (instance is null || instance.Members.Length != members.Length ||
                    members.Any(member => !ReferenceEquals(member.Actor.Session.PartyDungeon, instance) ||
                        member.Actor.Session.ReplayAntonLoading(dungeon) is not { Entry.RaidEntry: { } identity } ||
                        identity.InstanceId != start.InstanceId || identity.Phase != phase || identity.Squad != anchor.Squad)) return [];
                // 只重放请求者的原Run；不重新授权、不建Run、不改变全员37屏障。
                return [new(PartyDungeonNoticeKind.LoadingReplay,
                    party.Members.Single(member => ReferenceEquals(member.Actor.Session, leader)), party.Members,
                    leader.ReplayAntonLoading(dungeon))];
            }
            reason = "anton-entry-missing-maps";
            if (catalog.Find(dungeon) is not { } definition) return [];
            var mazes = definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0).ToArray();
            reason = "anton-entry-maze-unproven";
            if (mazes.Length != 1) return [];
            var selections = members.Select(member => new RaidDungeonSelection(new(start.InstanceId, member.Actor, phase,
                member.Squad, catalog, () => MemberCurrent(member), id => Allows(battle, id), kind) { Battle = battle },
                dungeon, mazes[0].SourceOrdinal)).ToArray();
            reason = "anton-entry-authorization-changed";
            if (selections.Any(selection => !selection.Admission.Actor.Session.CanBeginRaidDungeonSelection(selection.Admission))) return [];
            // 安装前记录城镇来源：导航板非零15从TOWN直发16，客户端未进入选图模块2，29的0238FBC4门不会走初图createMap。
            var fromTown = selections.Select(selection => selection.Admission.Actor.Session.Phase == SessionPhase.Town).ToArray();
            return selections.Select((selection, index) => new PartyDungeonNotice(PartyDungeonNoticeKind.RaidSelection,
                party.Members.Single(member => ReferenceEquals(member.Actor.Session, selection.Admission.Actor.Session)), party.Members,
                Selection: selection.Admission.Actor.Session.InstallRaidDungeonSelection(selection),
                SceneTransition: fromTown[index])).ToArray();
        });
        rejection = result.Length == 0 ? reason : null;
        return result;

        bool MemberCurrent(RaidMember member)
        {
            var current = registry.Find(member.Actor);
            return member.Actor.IsCurrent && RaidAdmissionPlan.Rejection(member.Actor.Session.ChannelEntry, member.Actor.Session.Selected!) is null &&
                current is { Started: { } started } && current.Type == (byte)kind && current.Key == raid.Key && started.InstanceId == start.InstanceId &&
                current.Members.Any(other => other.Actor == member.Actor && other.Squad == member.Squad);
        }
    }

    static bool Allows(AntonRaidBattle battle, int dungeon) => battle.Kind == RaidKind.Prey
        ? battle.AllowsPreyEntry(dungeon) : battle.AllowsAntonEntry(dungeon);
}
