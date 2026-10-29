using DfoServer.Game.Caching;
using System.Collections.Immutable;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Pvp;

namespace DfoServer.Game.Pvp;

internal sealed record PvpArenaChange(PvpArenaMembership Member, PvpArenaRoomView View, PvpArenaNotice[] Notices)
{
    public PvpArenaNotice? Initial { get; init; }
}

internal sealed partial class PvpArenaService
{
    readonly object gate = new();
    readonly GameCacheIndex<ushort, PvpArenaRoom> rooms;
    readonly GameCacheIndex<GameSession, PvpArenaMembership> members;
    readonly PvpArenaCatalog catalog;
    readonly IPvpArenaResultStore results;
    readonly PvpRatingSettlement? ratings;
    readonly PvpTotalTeamService? totalTeams;
    readonly PvpTotalRatingSettlement? totalRatings;
    readonly DfoServer.GameWorld.Characters.SkillLearningCatalog? totalSkills;
    ushort nextId;
    public event Action? Changed;
    public void PresenceChanged() => Changed?.Invoke();

    public PvpArenaService(GameCache memory, PvpArenaCatalog catalog, IPvpArenaResultStore results, int matchingCapacity = 4096,
        PvpRatingSettlement? ratings = null, PvpTotalTeamService? totalTeams = null,
        PvpTotalRatingSettlement? totalRatings = null, DfoServer.GameWorld.Characters.SkillLearningCatalog? totalSkills = null)
    {
        if (matchingCapacity is < 1 or > 65534) throw new ArgumentOutOfRangeException(nameof(matchingCapacity));
        this.catalog = catalog;
        this.results = results;
        this.ratings = ratings;
        this.totalTeams = totalTeams;
        this.totalRatings = totalRatings;
        this.totalSkills = totalSkills;
        this.matchingCapacity = matchingCapacity;
        rooms = memory.CreateIndex<ushort, PvpArenaRoom>();
        members = memory.CreateIndex<GameSession, PvpArenaMembership>();
        matching = memory.CreateIndex<GameSession, PvpMatchingTicket>();
    }

    public PvpArenaRoom? Find(ChannelIdentity channel, ushort id)
    {
        lock (gate) return rooms.TryGetValue(id, out var room) && !room.IsMatching && room.Channel == ChannelKey(channel) ? room : null;
    }

    public PvpArenaRoomView[] Directory(ChannelIdentity channel)
    {
        lock (gate) return rooms.SnapshotValues().Where(room => !room.IsMatching && room.Channel == ChannelKey(channel))
            .OrderBy(room => room.Id).Select(room => room.View()).ToArray();
    }

    // 成功时返回的新房间门禁已占用。Handler 必须在应答入队后 finally 释放，入房者才可取得它。
    // 这里只对尚未发布的新对象作非阻塞取锁，不会在本人门禁下等待已有房间。
    public PvpArenaChange? Create(GameSession session, string title, PvpArenaMode mode, ushort map, bool blocked = false,
        byte titlePreset = 0, string password = "")
    {
        var admission = session.CapturePvpArenaAdmission(blocked);
        if (admission is null || !Eligible(admission) || !catalog.IsMapIndex(map)) return null;
        return admission.Actor.Execute(() =>
        {
            if (!RecoverRatings(session)) return null;
            lock (gate)
            {
                if (!admission.Actor.IsCurrent() || !ReferenceEquals(admission.Actor.Character, session.Selected) ||
                    members.TryGetValue(session, out _) || AllocateId() == 0) return null;
                var room = new PvpArenaRoom(nextId, ChannelKey(admission.Identity.Channel), title, mode, map, titlePreset, password);
                if (!room.Commands.Wait(0)) throw new InvalidOperationException("新建决斗房间门禁已被占用");
                bool installed = false;
                try
                {
                    var seat = room.Join(admission.Actor.Owner.Account, admission.Actor.Character.Id, 0, job: admission.Actor.Character.Job)!;
                    var member = new PvpArenaMembership(room, seat.Token, admission.Actor, admission.Identity);
                    if (!session.InstallPvpArena(member)) return null;
                    rooms.Add(room.Id, room);
                    members.Add(session, member);
                    installed = true;
                    var view = room.View();
                    return new PvpArenaChange(member, view, []) { Initial = Notice(member, view) };
                }
                finally { if (!installed) room.Commands.Release(); }
            }
        });
    }

    // 调用前由分派器取得 expected.Commands，不能在 Handler 的本人门禁内等待它。
    public PvpArenaChange? Join(GameSession session, PvpArenaRoom expected, bool spectator, bool blocked = false,
        string? password = null)
    {
        var admission = session.CapturePvpArenaAdmission(blocked);
        if (admission is null || !Eligible(admission)) return null;
        return admission.Actor.Execute(() =>
        {
            if (!RecoverRatings(session)) return null;
            lock (gate)
            {
                if (!admission.Actor.IsCurrent() || !ReferenceEquals(admission.Actor.Character, session.Selected) ||
                    members.TryGetValue(session, out _) || !Registered(expected) || expected.IsMatching ||
                    expected.Channel != ChannelKey(admission.Identity.Channel) || !expected.AcceptsPassword(password)) return null;
                var current = expected.View().Members.Where(row => !row.Spectator).ToArray();
                byte team = (byte)(current.Count(row => row.Team == 0) <= current.Count(row => row.Team == 1) ? 0 : 1);
                var seat = expected.Join(admission.Actor.Owner.Account, admission.Actor.Character.Id, team, spectator, admission.Actor.Character.Job);
                if (seat is null) return null;
                var member = new PvpArenaMembership(expected, seat.Token, admission.Actor, admission.Identity);
                if (!session.InstallPvpArena(member)) { expected.Leave(seat.Token); return null; }
                members.Add(session, member);
                var view = expected.View();
                return new PvpArenaChange(member, view, Notices(expected, view).Where(notice =>
                    !ReferenceEquals(notice.Recipient, member)).Select(notice => notice with
                    { Kind = PvpArenaNoticeKind.Seats, Characters = [member.Identity] }).ToArray())
                    { Initial = Notice(member, view) };
            }
        });
    }

    public PvpArenaNotice[] SetReady(GameSession session, bool ready) => RecoverRatings(session) ? Change(session,
        member => !member.Room.IsMatching && member.Room.SetReady(member.Token, ready), PvpArenaNoticeKind.Ready) : [];

    public PvpArenaNotice[] Move(GameSession session, byte seat, byte team, bool spectator) => Change(session,
        member => !member.Room.IsMatching && member.Room.Move(member.Token, seat, team, spectator), PvpArenaNoticeKind.Seats);

    public PvpArenaNotice[] Configure(GameSession session, PvpArenaMode mode, ushort map) =>
        catalog.IsMapIndex(map) ? Change(session, member => !member.Room.IsMatching && member.Room.Configure(member.Token, mode, map), PvpArenaNoticeKind.State) : [];

    public PvpArenaNotice[] Start(GameSession session, DateTimeOffset now) => RecoverRatings(session) ? Change(session, member =>
        !member.Room.IsMatching && member.Room.Match is null && member.Room.Start(member.Token, now, TimeSpan.FromSeconds(30),
            catalog.BattleDurationFor(session.ChannelEntry!.Type)) is not null,
        PvpArenaNoticeKind.Start) : [];

    public bool RecoverRatings(GameSession session)
    {
        if (ratings is null) return true;
        var identity = session.OnlineCharacter();
        if (identity is null) return false;
        lock (gate)
            if (rooms.SnapshotValues().Any(room => room.Match is { CommittedRecord: null } match &&
                match.Fighters.Any(fighter => fighter.Account == identity.Account))) return false;
        return (totalRatings is null || session.RecoverPvpTotalRatings(totalRatings)) && session.RecoverPvpRatings(ratings);
    }

    public PvpArenaNotice[] Loaded(GameSession session, Guid match, DateTimeOffset now) => Change(session, member =>
        member.Room.Match?.MarkLoaded(match, member.Token, now) == true, PvpArenaNoticeKind.Loaded);

    // 客户端逐席报告1表示该席已无待连接peer；只检查本局其它参战者，空席/旁观席不参与屏障。
    public PvpArenaNotice[] Connected(GameSession session, Guid match, IReadOnlyList<bool> seats, DateTimeOffset now) =>
        Change(session, member => seats.Count == 8 && member.Room.Match is { } current &&
            current.Fighters.Where(row => row.Membership != member.Token).All(row => seats[row.Seat]) &&
            current.MarkConnected(match, member.Token, now), PvpArenaNoticeKind.Connected);

    public PvpArenaNotice[] Death(GameSession session, Guid match, ushort victim, ushort killer, DateTimeOffset? now = null) => Change(session, member =>
    {
        if (member.Actor.Character.Id != victim || member.Room.Match is not { } current) return false;
        var enemy = current.Fighters.FirstOrDefault(row => row.CharacterId == killer);
        return (killer == ushort.MaxValue || enemy is not null) && current.ReportDeath(match, member.Token, enemy?.Membership, now);
    }, PvpArenaNoticeKind.Death, killer);

    public PvpArenaNotice[] SelectTotal(GameSession session, Guid match, byte index, DateTimeOffset now) => Change(session,
        member => member.Room.Match?.SelectTotal(match, member.Token, index, now) == true, PvpArenaNoticeKind.TotalSelected);

    public PvpArenaNotice[] TotalProgress(GameSession session) => Change(session,
        member => member.Room.Match?.Total is not null, PvpArenaNoticeKind.TotalProgress);

    public PvpArenaNotice[] EndpointChanged(GameSession session) => Change(session, _ => true, PvpArenaNoticeKind.Endpoints);

    public bool Timeout(GameSession session, Guid match, IReadOnlyList<uint> values, DateTimeOffset now)
    {
        if (session.CurrentPvpArena is not { } member) return false;
        return member.Actor.Execute(() =>
        {
            lock (gate) return Current(member) && member.Room.Match?.ReportTimeout(match, member.Token, values, now) == true;
        });
    }

    // 调用方已持本房间Commands；全服索引锁内只固定快照，不在其中等SQLite writer。
    public PvpArenaResultRecord? RecordResult(GameSession session, DateTimeOffset now)
    {
        if (session.CurrentPvpArena is not { } member) return null;
        return member.Actor.Execute(() =>
        {
            PvpArenaMatch match;
            PvpArenaResultRecord? record;
            lock (gate)
            {
                if (!Current(member) || member.Room.Match is not { } current) return null;
                if (!current.Fighters.Any(row => row.Membership == member.Token)) return null;
                match = current;
                record = match.CommittedRecord ?? match.CaptureRecord(member.Room.Instance, member.Room.Channel, now,
                    member.Room.IsTotal ? PvpRatingMode.Total : member.Room.IsMatching ? PvpRatingMode.Matching : PvpRatingMode.Ordinary);
            }
            if (record is null) return null;
            var committed = match.CommittedRecord ?? results.CommitPvpArenaResult(member.Actor.Owner, record);
            lock (gate)
            {
                if (!Current(member) || !ReferenceEquals(member.Room.Match, match))
                    throw new InvalidOperationException("决斗回执提交期间原房间已变化");
                if (match.CommittedRecord is null) match.InstallRecord(committed);
            }
            if (ratings is not null && committed.RatingMode is PvpRatingMode.Ordinary or PvpRatingMode.Matching &&
                ratings.Recover(member.Actor.Owner, member.Actor.Character.Id))
            {
                var receipt = ratings.Settle(member.Actor.Owner, member.Actor.Character.Id, committed.MatchId, committed.RatingMode);
                lock (gate)
                {
                    if (!Current(member) || !ReferenceEquals(member.Room.Match, match))
                        throw new InvalidOperationException("经验回执提交期间原房间已变化");
                    match.InstallRatingReceipt(member.Token, receipt);
                }
            }
            else if (totalRatings is not null && committed.RatingMode == PvpRatingMode.Total && totalRatings.Recover(member.Actor.Owner))
            {
                var total = totalRatings.Settle(member.Actor.Owner, committed.MatchId);
                var receipt = total.Characters.Single(row => row.CharacterId == member.Actor.Character.Id);
                lock (gate)
                {
                    if (!Current(member) || !ReferenceEquals(member.Room.Match, match))
                        throw new InvalidOperationException("总力回执提交期间原房间已变化");
                    match.InstallRatingReceipt(member.Token, receipt);
                }
            }
            return committed;
        });
    }

    bool Eligible(PvpArenaAdmission admission) => catalog.CanEnter(PvpArenaEligibility.Ordinary,
        admission.Actor.Character.Job, (byte)(admission.Actor.Character.GrowType & 15));

    PvpArenaNotice[] Change(GameSession session, Func<PvpArenaMembership, bool> apply, PvpArenaNoticeKind kind,
        ushort killer = ushort.MaxValue)
    {
        if (session.CurrentPvpArena is not { } member) return [];
        return member.Actor.Execute(() =>
        {
            lock (gate)
            {
                if (!Current(member) || !apply(member)) return [];
                return Notices(member.Room, member.Room.View()).Select(notice => notice with
                {
                    Kind = kind, ReadySeat = member.Room.Member(member.Token)!.Seat,
                    Peer = member.Actor.Character.Id, VictimSeat = member.Room.Member(member.Token)!.Seat,
                    KillerSeat = member.Room.Match?.Fighters.FirstOrDefault(row => row.CharacterId == killer)?.Seat ?? byte.MaxValue,
                    Characters = kind == PvpArenaNoticeKind.Start ? notice.Characters : []
                }).ToArray();
            }
        });
    }

    public PvpArenaNotice[] Leave(GameSession session)
    {
        if (session.CurrentPvpArena is not { } member) return [];
        if (member.Room.IsMatching && member.Room.Match is null)
            return CancelMatchingRoom(member.Room, DateTimeOffset.UtcNow);
        return member.Actor.Execute(() =>
        {
            lock (gate)
            {
                if (!Current(member)) return [];
                member.Room.Match?.Depart(member.Room.Match.Id, member.Token);
            }
            // 主动退出仍在原owner下提交弃权事实；写失败时保留原成员，重试不会改写本局结果。
            RecordResult(session, DateTimeOffset.UtcNow);
            lock (gate)
            {
                if (!Current(member) || session.LeavePvpArena(member) is not { } departure) return [];
                return Remove(member, departure);
            }
        });
    }

    // 跨连接广播及结算不放进 Activity.Reset。旧owner也能按精确成员索引拆离原房间。
    public async Task<PvpArenaNotice[]> DetachAsync(GameSession session)
    {
        DetachMatching(session);
        PvpArenaMembership? member;
        lock (gate) { if (!members.TryGetValue(session, out member)) return []; }
        await member.Room.Commands.WaitAsync();
        try
        {
            if (member.Room.IsMatching && member.Room.Match is null)
                return CancelMatchingRoom(member.Room, DateTimeOffset.UtcNow);
            PvpArenaNotice[]? notices = null;
            if (member.Actor.Owner.IsCurrent)
            {
                try
                {
                    notices = member.Actor.Owner.Execute(() =>
                    {
                        if (member.IsCurrent) return Leave(session);
                        lock (gate) return Remove(member, null);
                    });
                }
                catch (InvalidOperationException) when (!member.Actor.Owner.IsCurrent) { }
            }
            if (notices is null)
                lock (gate) notices = Remove(member, null);
            // 被接管的旧owner只能拆离原成员，落库由仍有效的本局成员执行，不升级旧租约。
            var witness = notices.FirstOrDefault(row => row.Departure is null &&
                row.Recipient.IsCurrent && row.Recipient.Room.Match?.Fighters.Any(fighter =>
                    fighter.Membership == row.Recipient.Token) == true)?.Recipient;
            if (witness is not null) RecordResult(witness.Actor.Session, DateTimeOffset.UtcNow);
            return notices;
        }
        finally { member.Room.Commands.Release(); }
    }

    PvpArenaNotice[] Remove(PvpArenaMembership member, PvpArenaDeparture? departure)
    {
        if (!members.Remove(member.Actor.Session, member)) return [];
        member.Room.Leave(member.Token);
        if (member.Room.IsEmpty) rooms.Remove(member.Room.Id, member.Room);
        var view = member.Room.View();
        var notices = Notices(member.Room, view).Select(notice => notice with
            { Kind = PvpArenaNoticeKind.State, Characters = [] }).ToArray();
        return departure is null ? notices : [..notices, Notice(member, view) with { Departure = departure }];
    }

    public void Publish(IEnumerable<PvpArenaNotice> notices)
    {
        foreach (var notice in notices) notice.Recipient.Actor.Session.PvpArenaNoticeArrived?.Invoke(notice);
        PresenceChanged();
    }

    PvpArenaNotice[] Notices(PvpArenaRoom room, PvpArenaRoomView view) => members.SnapshotValues().Where(member =>
        ReferenceEquals(member.Room, room) && member.IsCurrent).Select(member => Notice(member, view)).ToArray();

    PvpArenaNotice Notice(PvpArenaMembership member, PvpArenaRoomView view) => new(member, view)
    {
        MatchingRatings = member.Room.Match?.Fighters.Where(fighter => fighter.Rating is not null)
            .ToImmutableDictionary(fighter => fighter.CharacterId, fighter => fighter.Rating!) ?? ImmutableDictionary<ushort, PvpRatingSnapshot>.Empty,
        TotalRevision = member.Room.Match?.Total?.Revision ?? 0,
        TotalChoices = member.Room.Match?.Total?.Choices ?? [],
        TotalRosters = member.Room.IsTotal ? member.Room.MatchingLoad!.Members.Select(ticket => ticket.TotalRoster!).ToImmutableArray() : [],
        Characters = members.SnapshotValues().Where(current => ReferenceEquals(current.Room, member.Room) && current.IsCurrent)
            .Select(current => current.Identity with { Character = current.Actor.Session.Selected ?? current.Actor.Character }).ToImmutableArray(),
        Endpoints = members.SnapshotValues().Where(current => ReferenceEquals(current.Room, member.Room) && current.IsCurrent &&
            current.Actor.Session.PartyEndpoint is not null).Select(current => new DfoServer.Game.Party.PartyEndpointRow(
                current.Actor.Character.Id, current.Actor.Session.PartyEndpoint!, current.Actor.Session.PeerAccount)).ToImmutableArray()
    };

    bool Current(PvpArenaMembership member) => member.IsCurrent && Registered(member.Room) &&
        members.TryGetValue(member.Actor.Session, out var current) && ReferenceEquals(member, current);
    bool Registered(PvpArenaRoom room) => rooms.TryGetValue(room.Id, out var current) && ReferenceEquals(room, current);
    static ushort ChannelKey(ChannelIdentity channel) => (ushort)(channel.ServerIndex << 8 | channel.Number);

    ushort AllocateId()
    {
        for (int attempt = 0; attempt < ushort.MaxValue - 1; attempt++)
        {
            nextId = (ushort)(nextId % (ushort.MaxValue - 1) + 1);
            if (!rooms.TryGetValue(nextId, out _)) return nextId;
        }
        return 0;
    }
}
