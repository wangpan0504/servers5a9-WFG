using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Party;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Guild;

internal sealed record GuildTeleportRequest(Location Target, byte Source);
internal sealed record GuildTeleportEntitlement(uint GuildId, long JoinedAtUnix, uint Remaining);
internal sealed record GuildTeleportPeer(GuildManagementIdentity Identity, byte Slot,
    Action<GuildTeleportStatus> Status, Func<TownTravelReceipt, Task> Travel, ulong SerialFloor);
internal sealed record GuildTeleportPosition(GuildTeleportPeer Peer, Character Expected, Location Destination, Location Return);
internal sealed record GuildTeleportCommitPlan(string RequestId, GuildTeleportEntitlement Entitlement,
    GuildTeleportPeer Leader, GuildTeleportPosition[] Positions, Func<bool> IsCurrent);
internal sealed record GuildTeleportCommit(ushort Error, Character[]? Characters = null, bool Replayed = false)
{
    public bool Success => Error == 0;
}
internal interface IGuildTeleportStore
{
    GuildTeleportEntitlement? ReadGuildTeleportEntitlement(AccountOwners.Lease owner, ushort characterId);
    GuildTeleportCommit CommitGuildTeleport(GuildTeleportCommitPlan plan);
}
internal sealed class GuildTeleportOffer(GuildTeleportRequest wire, GuildTeleportRequest request, string requestId,
    GuildTeleportPeer leader, GuildTeleportPeer[] peers, PartySnapshot? party, GuildTeleportEntitlement entitlement, long deadline)
{
    public GuildTeleportRequest Wire { get; } = wire;
    public GuildTeleportRequest Request { get; } = request;
    public string RequestId { get; } = requestId;
    public GuildTeleportPeer Leader { get; } = leader;
    public GuildTeleportPeer[] Peers { get; } = peers;
    public PartySnapshot? Party { get; } = party;
    public GuildTeleportEntitlement Entitlement { get; } = entitlement;
    public long Deadline { get; } = deadline;
    public Dictionary<ushort, byte> Answers { get; } = new() { [leader.Identity.CharacterId] = 1 };
    public int State;
    public byte Phase;
    public ITimer? Timer;
    public GuildTeleportPosition[]? Positions;
}
internal sealed record GuildTeleportStatus(GuildTeleportOffer Offer, byte Phase, (ushort Id, byte Slot, byte Answer)[] Answers);
internal sealed record GuildTeleportResult(ushort Error, GuildTeleportOffer? Offer = null,
    (GuildTeleportPeer Peer, TownTravelReceipt Travel)[]? Travels = null, bool Replayed = false)
{
    public bool Success => Error == 0;
}

internal sealed class GuildTeleportService(IGuildTeleportStore store, PartyService parties, TownCatalog towns,
    GuildManagementService management, TimeProvider? clock = null, Action<Exception>? onError = null) : IDisposable
{
    readonly object gate = new();
    readonly Dictionary<GameSession, GuildTeleportOffer> pending = new();
    readonly Dictionary<GameSession, GuildTeleportOffer> last = new();
    bool disposed;
    readonly TimeProvider time = clock ?? TimeProvider.System;
    long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    public GuildTeleportResult Begin(GameSession session, GuildTeleportRequest request, string requestId)
    {
        if (request.Source != 0 || request.Target.Direction != 5 || !GuildEconomyService.ValidRequestId(requestId)) return new(7);
        var party = parties.Current(session);
        var members = party?.Members;
        if (party is not null && (members!.Length is < 1 or > 4 ||
            !members.Any(member => ReferenceEquals(member.Actor.Session, session) && member.Slot == party.Leader))) return new(7);
        GuildTeleportPeer[] peers;
        if (members is null)
        {
            var own = session.CaptureGuildTeleport(0);
            if (own is null) return new(7);
            peers = [own];
        }
        else
        {
            var captures = members.Select(member => member.Actor.Session.CaptureGuildTeleport(member.Slot)).ToArray();
            if (captures.Any(peer => peer is null)) return new(7);
            peers = captures.Select(peer => peer!).OrderBy(peer => peer.Identity.CharacterId).ToArray();
        }
        var leader = peers.Single(peer => ReferenceEquals(peer.Identity.Session, session));
        if (peers.Any(peer => peer.Identity.Channel != leader.Identity.Channel) || peers.Select(peer => peer.Identity.Owner.Account).Distinct().Count() != peers.Length)
            return new(7);
        var entitlement = store.ReadGuildTeleportEntitlement(leader.Identity.Owner, leader.Identity.CharacterId);
        if (entitlement is null) return new(7);
        var wire = request;
        lock (gate)
        {
            if (disposed) return new(7);
            if (last.TryGetValue(session, out var previous) && previous.Leader.Identity.Epoch == leader.Identity.Epoch)
            {
                if (previous.RequestId == requestId)
                    return previous.Phase is not (2 or 3) && previous.Wire == wire && previous.Entitlement.GuildId == entitlement.GuildId &&
                        previous.Entitlement.JoinedAtUnix == entitlement.JoinedAtUnix ? new(0, previous, Replayed: true) : new(7);
                if (request.Target is { Town: 0, Area: 0, X: 0, Y: 0 }) request = request with { Target = previous.Request.Target };
            }
            if (pending.Count + peers.Length > 2048 || peers.Any(peer => pending.ContainsKey(peer.Identity.Session))) return new(7);
        }
        if (entitlement.Remaining == 0) return new(7);
        // 目的地与任务准入在窗口登记前验证，禁止为不可到达的位置询问队友。
        if (PreparePositions(peers, request.Target) is null) return new(7);
        var offer = new GuildTeleportOffer(wire, request, requestId, leader, peers, party, entitlement, checked(Now + 30000));
        lock (gate)
        {
            if (disposed || pending.Count + peers.Length > 2048 || peers.Any(peer => pending.ContainsKey(peer.Identity.Session))) return new(7);
            foreach (var peer in peers) pending.Add(peer.Identity.Session, offer);
            last[session] = offer;
            if (peers.Length == 1) offer.State = 1;
        }
        return new(0, offer);
    }

    public void Activate(GuildTeleportOffer offer)
    {
        if (offer.Peers.Length < 2) return;
        GuildTeleportStatus? notice;
        lock (gate)
        {
            if (offer.State != 0 || disposed) return;
            notice = Status(offer, 0);
            offer.Timer = time.CreateTimer(_ => TimeoutOffer(offer), null,
                TimeSpan.FromMilliseconds(Math.Max(1, offer.Deadline - Now)), Timeout.InfiniteTimeSpan);
        }
        Deliver(notice);
    }
    public GuildTeleportResult Confirm(GameSession session, byte answer, ulong serial)
    {
        GuildTeleportOffer? offer; GuildTeleportStatus? notice = null; bool complete = false;
        var identity = session.CaptureGuildManagement();
        if (identity is null || answer is not (0 or 1 or 3)) return new(7);
        lock (gate)
        {
            if (!pending.TryGetValue(session, out offer) || offer.State != 0) return new(7);
            var peer = offer.Peers.Single(value => ReferenceEquals(value.Identity.Session, session));
            if (peer.Identity.CharacterId != identity.CharacterId || peer.Identity.Epoch != identity.Epoch || serial <= peer.SerialFloor) return new(7);
            if (Now >= offer.Deadline || answer == 3) notice = CloseLocked(offer, 3);
            else if (answer == 0)
            {
                offer.Answers[identity.CharacterId] = 2; notice = CloseLocked(offer, 2);
            }
            else if (!ReferenceEquals(session, offer.Leader.Identity.Session))
            {
                offer.Answers[identity.CharacterId] = 1;
                complete = offer.Answers.Count == offer.Peers.Length;
                if (complete) { offer.State = 1; offer.Timer?.Dispose(); offer.Timer = null; }
                else notice = Status(offer, 0);
            }
        }
        if (notice is not null) Deliver(notice);
        return complete ? Complete(offer!) : new(0, offer);
    }

    public GuildTeleportResult Complete(GuildTeleportOffer offer)
    {
        lock (gate)
            if (disposed || offer.State != 1 || !pending.TryGetValue(offer.Leader.Identity.Session, out var registered) ||
                !ReferenceEquals(registered, offer)) return new(7, offer);
        var positions = PreparePositions(offer.Peers, offer.Request.Target);
        if (positions is null) return Abort(offer);
        offer.Positions = positions;
        bool Current() => !disposed && offer.State == 1 && Now < offer.Deadline && positions.All(position =>
            position.Peer.Identity.IsCurrent() && position.Peer.Identity.Session.IsGuildTeleportCurrent(position)) &&
            (offer.Party is null ? parties.Current(offer.Leader.Identity.Session) is null : parties.IsCurrent(offer.Party));
        GuildTeleportCommit committed;
        try
        {
            committed = AccountOwners.Lease.ExecuteGroup(offer.Peers.Select(peer => (peer.Identity.Owner, peer.Identity.CharacterId)).ToArray(), () =>
            {
                if (!Current()) return new GuildTeleportCommit(7);
                var result = store.CommitGuildTeleport(new(offer.RequestId, offer.Entitlement, offer.Leader, positions, Current));
                if (result.Success && !result.Replayed)
                    for (int i = 0; i < positions.Length; i++) positions[i].Peer.Identity.Session.InstallGuildTeleport(positions[i], result.Characters![i]);
                return result;
            });
        }
        catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
        {
            onError?.Invoke(error); return Abort(offer);
        }
        if (!committed.Success) return Abort(offer);
        lock (gate) CloseLocked(offer, 1);
        if (committed.Replayed) return new(0, offer, Replayed: true);
        var travels = positions.Select((position, index) => (position.Peer,
            new TownTravelReceipt(committed.Characters![index], position.Peer.Identity.Owner.Generation, position.Peer.Identity.Epoch))).ToArray();
        return new(0, offer, travels);
    }
    public async Task PublishAsync(GuildTeleportResult result)
    {
        if (!result.Success || result.Offer is not { } offer) return;
        if (result.Travels is null)
        {
            if (result.Replayed && offer.Peers.Length > 1)
            {
                GuildTeleportStatus status; lock (gate) status = Status(offer, offer.Phase);
                Deliver(status);
            }
            return;
        }
        GuildTeleportStatus complete; lock (gate) complete = Status(offer, 1);
        if (offer.Peers.Length > 1) Deliver(complete);
        foreach (var (peer, travel) in result.Travels)
            if (peer.Identity.IsCurrent()) await peer.Travel(travel);
        var state = management.Store.ReadGuildManagement(offer.Leader.Identity.Owner, offer.Leader.Identity.CharacterId);
        if (state is not null) management.Publish(new(0, state.Guild.Id, state.Revision, Members: state.Members));
    }

    GuildTeleportPosition[]? PreparePositions(GuildTeleportPeer[] peers, Location target)
    {
        if (target.Town == 10 || target.Direction != 5 || target.X > short.MaxValue || target.Y > short.MaxValue) return null;
        var positions = new List<GuildTeleportPosition>();
        foreach (var peer in peers)
        {
            var character = peer.Identity.Session.PrepareGuildTeleport(peer.Identity);
            if (character is null) return null;
            var completed = QuestEligibility.Completed(character);
            if (towns.DestinationAdmission(target, character.Level, completed) != "ready" ||
                towns.IsRoom(target, completed) || towns.IsGuildHall(target, completed) || !towns.IsPosition(target, completed)) return null;
            positions.Add(new(peer, character, target, towns.ResolveRoomReturn(target, character.Return, character.Level, completed)));
        }
        return positions.ToArray();
    }
    GuildTeleportResult Abort(GuildTeleportOffer offer)
    {
        GuildTeleportStatus notice; lock (gate) notice = CloseLocked(offer, Now >= offer.Deadline ? (byte)3 : (byte)2);
        if (offer.Peers.Length > 1) Deliver(notice);
        return new(7, offer);
    }
    static GuildTeleportStatus Status(GuildTeleportOffer offer, byte phase) => new(offer, phase,
        offer.Peers.Select(peer => (peer.Identity.CharacterId, peer.Slot, offer.Answers.GetValueOrDefault(peer.Identity.CharacterId))).ToArray());
    GuildTeleportStatus CloseLocked(GuildTeleportOffer offer, byte phase)
    {
        offer.State = 2; offer.Phase = phase; offer.Timer?.Dispose(); offer.Timer = null;
        foreach (var peer in offer.Peers)
            if (pending.TryGetValue(peer.Identity.Session, out var registered) && ReferenceEquals(registered, offer)) pending.Remove(peer.Identity.Session);
        return Status(offer, phase);
    }
    static void Deliver(GuildTeleportStatus notice)
    {
        foreach (var peer in notice.Offer.Peers)
            if (peer.Identity.IsCurrent()) peer.Status(notice);
    }
    void TimeoutOffer(GuildTeleportOffer offer)
    {
        try
        {
            GuildTeleportStatus? notice;
            lock (gate) notice = offer.State == 0 ? CloseLocked(offer, 3) : null;
            if (notice is not null) Deliver(notice);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException)) { onError?.Invoke(error); }
    }
    public void Close(GameSession session)
    {
        GuildTeleportStatus? notice;
        lock (gate)
        {
            last.Remove(session);
            notice = pending.TryGetValue(session, out var offer) && offer.State == 0 ? CloseLocked(offer, 2) : null;
        }
        if (notice is not null && notice.Offer.Peers.Length > 1) Deliver(notice);
    }
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var offer in pending.Values.Distinct().ToArray()) CloseLocked(offer, 2);
            pending.Clear(); last.Clear();
        }
    }
}
