using DfoServer.Game.Accounts;
using DfoServer.Game.Caching;
using DfoServer.Game.Session;
using DfoServer.Game.Social;

namespace DfoServer.Game.Party;

internal sealed record PartyMember(SocialActor Actor, PublicCharacterSnapshot Identity, byte Slot)
{
    public bool IsCurrent() => Actor.IsCurrent() && Actor.Session.Phase is
        SessionPhase.Town or SessionPhase.MapSelect or SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement;
}
internal sealed record PartySnapshot(ushort Id, PartyMember[] Members, byte Leader, long Revision)
{
    public SemaphoreSlim Commands { get; } = new(1, 1);
    public PartySettings Settings { get; init; } = PartySettings.Default;
}
internal enum PartyNoticeKind { Invite, Accepted, Refused, Updated, Removed, Endpoints, SceneChanged, LeaderChanged }
internal sealed record PartyNotice(PartyNoticeKind Kind, PartyMember Recipient, PartyMember? Peer = null,
    PartySnapshot? Party = null, uint PeerId = 0, Func<bool>? Guard = null);
internal sealed record PartyResult(ushort Error, PartyNotice[] Notices)
{
    public bool Accepted => Error == 0;
}

internal sealed class PartyService
{
    sealed class Invitation(PartyMember source, PartyMember target, uint peer, DateTimeOffset expires)
    {
        public readonly PartyMember Source = source, Target = target;
        public readonly uint Peer = peer;
        public readonly DateTimeOffset Expires = expires;
        public volatile bool Active = true;
        public bool Current(DateTimeOffset now) => Active && now < Expires && Source.IsCurrent() && Target.IsCurrent();
    }
    readonly ISocialStore social;
    readonly TimeProvider timer;
    readonly object gate = new();
    readonly GameCacheIndex<ushort, PartySnapshot> parties;
    readonly GameCacheIndex<ushort, Invitation> invitations;
    ushort nextId;
    long revision;
    readonly HashSet<ushort> reservedRaidSquads = [];
    public PartyService(ISocialStore social, TimeProvider? clock = null, GameCache? memory = null)
    {
        this.social = social;
        timer = clock ?? TimeProvider.System;
        var cache = memory ?? new GameCache();
        parties = cache.CreateIndex<ushort, PartySnapshot>();
        invitations = cache.CreateIndex<ushort, Invitation>();
    }
    public event Action? Changed;
    public void PresenceChanged() => Changed?.Invoke();

    public PartySnapshot[] Snapshot() { lock (gate) return parties.SnapshotValues(); }

    // 普通Party仍是每分队一至四人；全团由开战用例固定owner后一次安装，不复用招募mode10邀请。
    internal (DfoServer.Game.Raids.RaidStarted Start, System.Collections.Immutable.ImmutableArray<DfoServer.Game.Characters.Character> Characters, PartyNotice[] Notices)?
        CreateRaidSquads(DfoServer.Game.Raids.RaidParty raid,
            Func<(DfoServer.Game.Raids.RaidStarted Start, System.Collections.Immutable.ImmutableArray<DfoServer.Game.Characters.Character> Characters)> commit)
    {
        var captured = raid.Members.OrderBy(m => m.Slot).Select(m => (Raid: m, Member: Capture(m.Actor.Session))).ToArray();
        var squads = new List<PartySnapshot>();
        lock (gate)
        {
            if (captured.Any(m => m.Member is null || Find(m.Member) is not null)) return null;
            foreach (var group in captured.GroupBy(m => m.Raid.Squad).OrderBy(g => g.Key))
            {
                if (group.Key == 0 || group.Count() > 4) return null;
                if (AllocateId() == ushort.MaxValue)
                { foreach (var reserved in squads) reservedRaidSquads.Remove(reserved.Id); return null; }
                reservedRaidSquads.Add(nextId);
                var members = group.Select((m, slot) => m.Member! with { Slot = (byte)slot }).ToArray();
                squads.Add(new(nextId, members, 0, ++revision) { Settings = PartySettings.Default with
                    { TitleCode = 0, Title = $"Party : {group.Key}", Capacity = 4, Dungeon = uint.MaxValue, Difficulty = 5 } });
            }
        }
        try
        {
            var committed = commit();
            lock (gate)
            {
                foreach (var squad in squads)
                {
                    foreach (var member in squad.Members) member.Actor.Session.AdvancePartyMembership();
                    parties.Set(squad.Id, squad);
                }
            }
            return (committed.Start, committed.Characters, squads.SelectMany(p => p.Members.Select(m => Updated(p, m))).ToArray());
        }
        finally { lock (gate) foreach (var squad in squads) reservedRaidSquads.Remove(squad.Id); }
    }

    ushort AllocateId()
    {
        for (int i = 0; i < ushort.MaxValue; i++)
        {
            nextId = (ushort)((nextId + 1) % ushort.MaxValue);
            if (!parties.TryGetValue(nextId, out _) && !reservedRaidSquads.Contains(nextId)) return nextId;
        }
        return ushort.MaxValue;
    }

    public PartyResult SetInformation(GameSession session, byte operation, PartySettings settings, bool blocked = false)
    {
        // ACK12的21分支为本版已确认的拒绝，不借用其它版本的错误提示。
        if (blocked || Capture(session) is not { } member || operation > 1 || settings.Capacity is < 1 or > 4 ||
            settings.TitleCode == 0 && string.IsNullOrWhiteSpace(settings.Title))
            return Reject(21);
        return member.Actor.Execute(() =>
        {
            lock (gate)
            {
                if (!member.IsCurrent() || !session.CanChangeTownParty) return Reject(21);
                var party = Find(member);
                // 原生快捷改名请求不携带招募选项；省略时沿用已提交设置。
                settings = settings with { Option = settings.Option ?? party?.Settings.Option ?? PartySettings.Default.Option };
                if (party is null)
                {
                    if (operation != 0 || AllocateId() == ushort.MaxValue) return Reject(21);
                    session.AdvancePartyMembership();
                    party = new(nextId, [member], 0, ++revision) { Settings = settings };
                }
                else
                {
                    if (party.Members.Single(m => ReferenceEquals(m.Actor.Session, session)).Slot != party.Leader ||
                        party.Members.Length > settings.Capacity || party.Members.Any(m => !m.IsCurrent() || m.Actor.Session.Phase != SessionPhase.Town))
                        return Reject(21);
                    if (party.Settings == settings) return new(0, []);
                    party = party with { Settings = settings, Revision = ++revision };
                }
                parties.Set(party.Id, party);
                return new PartyResult(0, party.Members.Select(m => Updated(party, m)).ToArray());
            }
        });
    }

    public PartySnapshot? Current(GameSession session)
    {
        lock (gate) return parties.SnapshotValues().FirstOrDefault(p => p.Members.Any(m =>
            ReferenceEquals(m.Actor.Session, session) && m.IsCurrent()));
    }
    public bool IsCurrent(PartySnapshot party)
    {
        lock (gate) return parties.TryGetValue(party.Id, out var current) && ReferenceEquals(current, party) &&
            party.Members.All(m => m.IsCurrent());
    }

    static PartyMember? Capture(GameSession session)
    {
        if (!session.CanChangeTownParty || session.CaptureSocialActor() is not { } actor || session.PublicCharacter(actor.Character.Id) is not { } identity ||
            identity.Phase != SessionPhase.Town) return null;
        return new(actor, identity, 0);
    }
    PartySnapshot? Find(PartyMember member) => parties.SnapshotValues().FirstOrDefault(p => p.Members.Any(m =>
        ReferenceEquals(m.Actor.Session, member.Actor.Session) && m.Actor.Epoch == member.Actor.Epoch));
    public ushort PartyId(ushort character)
    {
        lock (gate) return parties.SnapshotValues().FirstOrDefault(p => p.Members.Any(m => m.Actor.Character.Id == character && m.IsCurrent()))?.Id ?? ushort.MaxValue;
    }
    public bool HasMultipleMembers(GameSession session)
    {
        lock (gate) return parties.SnapshotValues().Any(p => p.Members.Count(m => m.IsCurrent()) > 1 &&
            p.Members.Any(m => ReferenceEquals(m.Actor.Session, session) && m.IsCurrent()));
    }
    static PartyResult Reject(ushort code = 3) => new(code, []);
    static PartyResult Pair(PartyMember a, PartyMember b, Func<PartyResult> action)
    {
        try { return AccountOwners.Lease.ExecuteTogether(a.Actor.Owner, b.Actor.Owner, action); }
        catch (InvalidOperationException) when (!a.Actor.Owner.IsCurrent || !b.Actor.Owner.IsCurrent) { return Reject(); }
    }
    bool Blocked(PartyMember a, PartyMember b) => social.IsBlocked(a.Actor.Character.Id, b.Actor.Character.Id) ||
        social.IsBlocked(b.Actor.Character.Id, a.Actor.Character.Id);
    public PartyResult Invite(GameSession source, GameSession target, uint peer, bool blocked = false)
    {
        var a = Capture(source); var b = Capture(target);
        if (blocked || a is null || b is null || source.StartedRaid is not null || target.StartedRaid is not null ||
            a.Actor.Owner.Account == b.Actor.Owner.Account || a.Identity.Channel != b.Identity.Channel || Blocked(a, b))
            return Reject();
        return Pair(a, b, () =>
        {
            lock (gate)
            {
                var now = timer.GetUtcNow();
                foreach (var old in invitations.SnapshotValues().Where(i => !i.Current(now)).ToArray())
                {
                    old.Active = false; invitations.Remove(old.Target.Actor.Character.Id);
                }
                if (!a.IsCurrent() || !b.IsCurrent() || !source.CanChangeTownParty || !target.CanChangeTownParty) return Reject();
                var pa = Find(a); var pb = Find(b);
                if (pa is not null && pb is not null || pa is not null && pa.Members.Length >= pa.Settings.Capacity ||
                    pb is not null && pb.Members.Length >= pb.Settings.Capacity) return Reject(4);
                if (pa is not null && pa.Leader != pa.Members.Single(m => ReferenceEquals(m.Actor.Session, source)).Slot ||
                    pb is not null && pb.Leader != pb.Members.Single(m => ReferenceEquals(m.Actor.Session, target)).Slot) return Reject(4);
                if (invitations.TryGetValue(b.Actor.Character.Id, out var existing))
                    return existing.Source.Actor == a.Actor && existing.Peer == peer ? new PartyResult(0, []) : Reject(4);
                var invite = new Invitation(a, b, peer, now.AddSeconds(30));
                invitations.Add(b.Actor.Character.Id, invite);
                return new PartyResult(0, [new(PartyNoticeKind.Invite, b, a, PeerId: peer,
                    Guard: () => invite.Current(timer.GetUtcNow()))]);
            }
        });
    }
    public PartyResult Respond(GameSession target, ushort sourceId, uint peer, bool accept, bool blocked = false)
    {
        if (blocked || !target.CanChangeTownParty) return Reject();
        Invitation? invite;
        lock (gate)
        {
            invitations.TryGetValue(target.Selected?.Id ?? 0, out invite);
            // 客户端可连续发送两次接受；已经同队的当前会话只确认完成，不重发名册。
            if (invite is null && accept && sourceId != target.Selected?.Id && parties.SnapshotValues().Any(p =>
                p.Members.Any(m => ReferenceEquals(m.Actor.Session, target) && m.IsCurrent()) &&
                p.Members.Any(m => m.Actor.Character.Id == sourceId && m.IsCurrent()))) return new(0, []);
        }
        // peer 是发送方自身的连接数据，接受者不回显邀请中的值。
        if (invite is null || invite.Source.Actor.Character.Id != sourceId ||
            !ReferenceEquals(invite.Target.Actor.Session, target) || !invite.Current(timer.GetUtcNow()) || Blocked(invite.Source, invite.Target)) return Reject();
        var a = invite.Source; var b = invite.Target;
        return Pair(a, b, () =>
        {
            lock (gate)
            {
                if (!invite.Current(timer.GetUtcNow()) || !a.Actor.Session.CanChangeTownParty ||
                    !b.Actor.Session.CanChangeTownParty || a.Actor.Session.StartedRaid is not null || b.Actor.Session.StartedRaid is not null ||
                    !invitations.TryGetValue(b.Actor.Character.Id, out var current) || !ReferenceEquals(current, invite))
                    return Reject();
                invite.Active = false; invitations.Remove(b.Actor.Character.Id);
                if (!accept) return new PartyResult(0, [new(PartyNoticeKind.Refused, a, b,
                    Guard: () => a.IsCurrent() && !invite.Active)]);
                var pa = Find(a); var pb = Find(b);
                if (pa is not null && pb is not null) return Reject(4);
                var party = pa ?? pb;
                if (party is not null && party.Members.Any(m => m.Actor.Session.Phase != SessionPhase.Town)) return Reject(4);
                if (party is not null && party.Members.Length >= party.Settings.Capacity) return Reject(4);
                if (party is not null)
                {
                    var leader = party.Members.Single(m => m.Slot == party.Leader);
                    if (!ReferenceEquals(leader.Actor.Session, pa is null ? target : a.Actor.Session)) return Reject(4);
                    var newcomer = pa is null ? a : b;
                    newcomer.Actor.Session.AdvancePartyMembership();
                    byte slot = Enumerable.Range(0, 4).Select(i => (byte)i).First(i => party.Members.All(m => m.Slot != i));
                    party = party with { Members = [..party.Members, newcomer with { Slot = slot }], Revision = ++revision };
                }
                else
                {
                    if (AllocateId() == ushort.MaxValue) return Reject(4);
                    a.Actor.Session.AdvancePartyMembership();
                    b.Actor.Session.AdvancePartyMembership();
                    party = new(nextId, [a, b with { Slot = 1 }], 0, ++revision);
                }
                parties.Set(party.Id, party);
                return new PartyResult(0, [..party.Members.Select(m => Updated(party, m)),
                    new(PartyNoticeKind.Accepted, a, b, PeerId: peer, Guard: () => Latest(party, a))]);
            }
        });
    }
    public PartyResult TransferLeader(GameSession session, byte slot, bool blocked = false)
    {
        if (blocked || Capture(session) is not { } member) return Reject();
        PartySnapshot? expected;
        PartyMember? target;
        lock (gate)
        {
            expected = Find(member);
            target = expected?.Members.FirstOrDefault(m => m.Slot == slot);
        }
        if (expected is null || target is null || ReferenceEquals(target.Actor.Session, session)) return Reject();
        return Pair(member, target, () =>
        {
            lock (gate)
            {
                // 获取双方 owner 后重新核对快照，不能委任给已离队、换角或被接管的会话。
                if (!ReferenceEquals(Find(member), expected) || !member.IsCurrent() || !target.IsCurrent() ||
                    !session.CanChangeTownParty || !target.Actor.Session.CanChangeTownParty ||
                    expected.Members.Any(m => !m.IsCurrent() || m.Actor.Session.Phase != SessionPhase.Town) ||
                    expected.Members.Single(m => ReferenceEquals(m.Actor.Session, session)).Slot != expected.Leader)
                    return Reject();
                var party = expected with { Leader = slot, Revision = ++revision };
                parties.Set(party.Id, party);
                return new PartyResult(0, party.Members.Select(m => new PartyNotice(PartyNoticeKind.LeaderChanged, m,
                    Party: party, Guard: () => Latest(party, m))).ToArray());
            }
        });
    }
    public PartyResult Leave(GameSession session, byte? kickSlot = null, bool blocked = false)
    {
        if (blocked || Capture(session) is not { } member) return Reject(19);
        return member.Actor.Execute(() =>
        {
            lock (gate)
            {
                if (!member.IsCurrent() || !session.CanChangeTownParty || session.StartedRaid is not null) return Reject(19);
                var party = Find(member);
                if (party is null) return Reject(19);
                var own = party.Members.Single(m => ReferenceEquals(m.Actor.Session, session));
                if (kickSlot is not null && (party.Leader != own.Slot || kickSlot == own.Slot)) return Reject(4);
                var removed = kickSlot is { } slot ? party.Members.FirstOrDefault(m => m.Slot == slot) : own;
                if (removed is null) return Reject(19);
                return new PartyResult(0, Remove(party, [removed]));
            }
        });
    }
    PartyNotice[] Remove(PartySnapshot party, PartyMember[] removed)
    {
        var remaining = party.Members.Except(removed).ToArray();
        var notices = removed.Select(m =>
        {
            long membership = m.Actor.Session.AdvancePartyMembership();
            return new PartyNotice(PartyNoticeKind.Removed, m, Party: party,
                Guard: () => m.Actor.IsCurrent() && m.Actor.Session.PartyMembershipVersion == membership && Absent(m));
        }).ToList();
        if (remaining.Length == 0) parties.Remove(party.Id);
        else
        {
            party = party with { Members = remaining, Leader = remaining.Any(m => m.Slot == party.Leader) ? party.Leader : remaining.Min(m => m.Slot),
                Revision = ++revision };
            parties.Set(party.Id, party);
            notices.AddRange(remaining.Select(m => Updated(party, m)));
        }
        return notices.ToArray();
    }
    bool Latest(PartySnapshot party, PartyMember member, SessionPhase phase = SessionPhase.Town)
    {
        lock (gate) return member.IsCurrent() && member.Actor.Session.Phase == phase &&
            parties.TryGetValue(party.Id, out var current) && ReferenceEquals(current, party);
    }
    bool Absent(PartyMember member) { lock (gate) return Find(member) is null; }
    PartyNotice Updated(PartySnapshot party, PartyMember member)
    {
        var phase = member.Actor.Session.Phase;
        return new(PartyNoticeKind.Updated, member, Party: party, Guard: () => Latest(party, member, phase));
    }
    public PartyNotice[] EndpointChanged(GameSession session)
    {
        lock (gate)
        {
            var party = parties.SnapshotValues().FirstOrDefault(p => p.Members.Any(m => ReferenceEquals(m.Actor.Session, session) && m.IsCurrent()));
            return party is null ? [] : party.Members.Select(m => new PartyNotice(PartyNoticeKind.Endpoints, m, Party: party,
                Guard: () => Latest(party, m))).ToArray();
        }
    }
    public PartyNotice[] SceneChanged(GameSession session)
    {
        lock (gate)
        {
            var party = parties.SnapshotValues().FirstOrDefault(p => p.Members.Any(m => ReferenceEquals(m.Actor.Session, session) && m.IsCurrent()));
            var member = party?.Members.Single(m => ReferenceEquals(m.Actor.Session, session));
            return party is null || member is null ? [] : [new(PartyNoticeKind.SceneChanged, member, Party: party,
                Guard: () => Latest(party, member))];
        }
    }
    public void Detach(GameSession session) => Publish(DetachNotices(session));

    // 生命周期用例在原owner内提交多个聚合，通知必须等owner释放后发布。
    internal PartyNotice[] DetachNotices(GameSession session)
    {
        List<PartyNotice> notices = [];
        lock (gate)
        {
            foreach (var invite in invitations.SnapshotValues().Where(i => ReferenceEquals(i.Source.Actor.Session, session) ||
                ReferenceEquals(i.Target.Actor.Session, session)).ToArray())
            {
                invite.Active = false; invitations.Remove(invite.Target.Actor.Character.Id);
            }
            foreach (var party in parties.SnapshotValues().ToArray())
            {
                var removed = party.Members.Where(m => ReferenceEquals(m.Actor.Session, session)).ToArray();
                if (removed.Length != 0) notices.AddRange(Remove(party, removed));
            }
        }
        return notices.ToArray();
    }
    public void Publish(IEnumerable<PartyNotice> notices)
    {
        foreach (var notice in notices)
            if (notice.Recipient.Actor.IsCurrent()) notice.Recipient.Actor.Session.PartyNoticeArrived?.Invoke(notice);
        PresenceChanged();
    }
}
