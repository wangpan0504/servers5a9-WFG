using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 正式进图只读原 Battle 当前开放图。阶段迁移由 Battle.PhaseProgress 决定，本服务不解释计时或机关。
internal sealed class AntonRaidDungeonService(RaidRegistry registry, PartyService parties,
    RaidDungeonCatalog catalog, bool enabled, RaidKind kind = RaidKind.Anton, RaidDungeonCatalog? preyTeam = null)
{
    public bool Enabled => enabled;
    public IReadOnlyList<int> MissingMaps => catalog.MissingMaps;
    public bool Contains(int dungeon) => catalog.DungeonPaths.ContainsKey(dungeon) || preyTeam?.DungeonPaths.ContainsKey(dungeon) == true;

    // 单人只借用既有共享副本的成员快照，不登记普通Party，也不改变攻坚标记。
    internal PartySnapshot? CapturePreySoloScene(GameSession session)
    {
        if (kind != RaidKind.Prey || parties.Current(session) is not null || session.CapturePreySoloSceneIdentity() is not { } scene ||
            session.CaptureSocialActor() is not { } actor || session.PublicCharacter(actor.Character.Id) is not { } identity) return null;
        return new(ushort.MaxValue, [new(actor, identity, 0)], 0, 0)
            { PreyBattleId = scene.Instance, PreyBoardingArea = scene.Area };
    }

    public PartyDungeonNotice[] Select(GameSession leader, int dungeon, PartySnapshot? party, out string? rejection,
        bool replayLoading = false, PreyLandingCandidate? landing = null)
    {
        rejection = "anton-entry-waiting-client-resources";
        if (!enabled) return [];
        rejection = "anton-entry-squad-mismatch";
        var raid = registry.FindCurrent(leader, leader.SelectionEpoch);
        bool solo = kind == RaidKind.Prey && raid is { Guide: false } && party is { Id: ushort.MaxValue, Members.Length: 1 } &&
            parties.Current(leader) is null;
        if (party is null || !solo && !parties.IsCurrent(party) || raid is not { Started: { } start } || raid.Type != (byte)kind ||
            !party.Members.Any(member => member.Slot == party.Leader && ReferenceEquals(member.Actor.Session, leader))) return [];
        var selectedCatalog = kind == RaidKind.Prey && !raid.Guide ? preyTeam : catalog;
        if (selectedCatalog is null) { rejection = "prey-team-entry-unavailable"; return []; }
        bool preyBoarding = kind == RaidKind.Prey && !raid.Guide;
        var anchor = raid.Members.SingleOrDefault(member => ReferenceEquals(member.Actor.Session, leader));
        if (anchor is null || !preyBoarding && anchor.Squad is < 1 or > 5) return [];
        if (preyBoarding && (party.PreyBattleId != start.InstanceId || party.PreyBoardingArea is not (4 or 5))) return [];
        // 普雷标记只用于名册；真实参战名单、顺序和队长来自当前左右空间的普通Party。
        var members = preyBoarding ? party.Members.OrderBy(member => member.Slot == party.Leader ? -1 : member.Slot)
            .Select(member => raid.Members.SingleOrDefault(other => ReferenceEquals(other.Actor.Session, member.Actor.Session)))
            .OfType<RaidMember>().ToArray() : raid.Members.Where(member => member.Squad == anchor.Squad).OrderBy(member => member.Slot).ToArray();
        if (members.Length is < 1 or > 4 || members[0] != anchor || members.Length != party.Members.Length ||
            members.Any(member => !party.Members.Any(other => ReferenceEquals(member.Actor.Session, other.Actor.Session) &&
                ReferenceEquals(member.Actor.Owner, other.Actor.Owner) && member.Actor.Epoch == other.Actor.Epoch &&
                member.Actor.Character == other.Actor.Character.Id))) return [];
        string? reason = "anton-entry-instance-mismatch";
        var result = AccountOwners.Lease.ExecuteGroup(members.Select(member => (member.Actor.Owner, member.Actor.Character)).ToArray(), () =>
        {
            if (!solo && !parties.IsCurrent(party) || members.Any(member => !MemberCurrent(member))) return [];
            if (solo && (leader.CapturePreySoloSceneIdentity() is not { } scene ||
                scene.Instance != party.PreyBattleId || scene.Area != party.PreyBoardingArea)) return [];
            var battle = leader.CurrentRaidBattle(anchor.Actor);
            if (battle is null || battle.Kind != kind || battle.Instance != start.InstanceId) return [];
            if (landing is not null && (landing.Dungeon != dungeon || !battle.IsPreyLandingCurrent(landing) ||
                members.Any(member => member.Actor.Session.Selected?.Position is not { Town: 59 } spot || spot.Area != landing.Side)))
            { reason = "prey-landing-arrival-changed"; return []; }
            // Reward后读取原聚合的实际phase；固定0会使已开放218/219被旧阶段授权拒绝。
            int phase = battle.View.Phase;
            reason = "anton-entry-dungeon-not-open";
            if (!Allows(battle, dungeon)) return [];
            reason = "prey-entry-side-mismatch";
            if (preyBoarding && !battle.AllowsPreyEntryFromSide(dungeon, party.PreyBoardingArea)) return [];
            reason = "prey-member-retry-wait";
            if (preyBoarding && members.Any(member => !battle.PreyMemberRetryReady(member.Actor, dungeon))) return [];
            if (replayLoading)
            {
                reason = "anton-entry-loading-identity-changed";
                var instance = leader.PartyDungeon;
                if (instance is null || instance.Members.Length != members.Length ||
                    members.Any(member => !ReferenceEquals(member.Actor.Session.PartyDungeon, instance) ||
                        member.Actor.Session.ReplayAntonLoading(dungeon) is not { Entry.RaidEntry: { } identity } ||
                        identity.InstanceId != start.InstanceId || identity.Phase != phase ||
                        (preyBoarding ? solo ? identity.PreySoloCharacter != anchor.Actor.Character : identity.PreyPartyId != party.Id :
                            identity.Squad != anchor.Squad))) return [];
                // 只重放请求者的原Run；不重新授权、不建Run、不改变全员37屏障。
                return [new(PartyDungeonNoticeKind.LoadingReplay,
                    party.Members.Single(member => ReferenceEquals(member.Actor.Session, leader)), party.Members,
                    leader.ReplayAntonLoading(dungeon))];
            }
            reason = "anton-entry-missing-maps";
            if (selectedCatalog.Find(dungeon) is not { } definition) return [];
            var mazes = definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0).ToArray();
            reason = "anton-entry-maze-unproven";
            if (mazes.Length != 1) return [];
            var selections = members.Select(member => new RaidDungeonSelection(new(start.InstanceId, member.Actor, phase,
                preyBoarding ? (byte)0 : member.Squad, selectedCatalog, () => MemberCurrent(member),
                id => Allows(battle, id) && (!preyBoarding || battle.PreyMemberRetryReady(member.Actor, id)), kind)
                { Battle = battle, PreyGuide = raid.Guide, PreyLanding = landing, PreyPartyId = preyBoarding && !solo ? party.Id : null,
                    PreySoloCharacter = solo ? member.Actor.Character : null },
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
                current.Members.Any(other => other.Actor == member.Actor && other.Squad == member.Squad) &&
                (!preyBoarding || (solo ? parties.Current(member.Actor.Session) is null :
                    parties.Current(member.Actor.Session) is { } actual && actual.Id == party.Id && actual.PreyBattleId == start.InstanceId));
        }
    }

    static bool Allows(AntonRaidBattle battle, int dungeon) => battle.Kind == RaidKind.Prey
        ? battle.AllowsPreyEntry(dungeon) : battle.AllowsAntonEntry(dungeon);
}
