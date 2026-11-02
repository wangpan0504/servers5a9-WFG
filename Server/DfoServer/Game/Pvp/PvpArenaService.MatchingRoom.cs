using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Session;

namespace DfoServer.Game.Pvp;

internal sealed record PvpMatchingRoomEntry(PvpArenaRoom Room, PvpMatchingLoadView Load,
    ImmutableArray<PvpArenaNotice> Initial)
{
    public ImmutableArray<PvpRatingSnapshot> Ratings { get; init; } = [];
}

internal sealed partial class PvpArenaService
{
    // 返回时持新房间Commands；调用方将双方初始通知入队后释放，不能持门禁等socket。
    public PvpMatchingRoomEntry? PrepareOrdinaryMatching(PvpMatchingLoadView expected, ushort map, DateTimeOffset now)
    {
        if (!catalog.IsMapIndex(map) || expected.Members.Length != 2) return null;
        var load = expected.Members[0].Load;
        if (load is null || load.Id != expected.Id) return null;
        try
        {
            return AccountOwners.Lease.ExecuteTogether(load.Members[0].Actor.Owner, load.Members[1].Actor.Owner, () =>
            {
                var displayRatings = ImmutableArray.CreateBuilder<PvpRatingSnapshot>(2);
                if (ratings is not null)
                    foreach (var ticket in load.Members)
                    {
                        if (!RecoverRatings(ticket.Actor.Session)) return null;
                        displayRatings.Add(ticket.TotalRoster is { } total && totalRatings is not null
                            ? totalRatings.Capture(ticket.Actor.Owner, total.Team.Id)
                            : ratings.Capture(ticket.Actor.Owner, ticket.Actor.Character.Id));
                    }
                lock (gate)
                {
                    if (!Registered(load) || load.Room is not null || load.Phase != PvpMatchingLoadPhase.Loading ||
                        now >= load.Deadline || load.Members.Any(ticket =>
                            !ticket.Actor.Session.CanTransferPvpMatching(ticket) || members.TryGetValue(ticket.Actor.Session, out _)) ||
                        AllocateId() == 0) return null;
                    var room = new PvpArenaRoom(nextId, ChannelKey(load.Characters[0].Channel), "", PvpArenaMode.Individual,
                        map, titlePreset: 1, matchingLoad: load);
                    if (!room.Commands.Wait(0)) throw new InvalidOperationException("新匹配房间门禁已被占用");
                    bool installed = false;
                    try
                    {
                        var entered = load.Members.Select((ticket, at) =>
                        {
                            var identity = load.Characters[at];
                            var actor = ticket.Actor with { Character = identity.Character };
                            var seat = room.Join(actor.Owner.Account, actor.Character.Id, (byte)at, job: actor.Character.Job,
                                questBindings: Quests.QuestPvpProgress.Bindings(actor.Character.ActiveMainQuests, quests)) ??
                                throw new InvalidOperationException("已核验的匹配成员无法建立席位");
                            return new PvpArenaMembership(room, seat.Token, actor, identity);
                        }).ToImmutableArray();
                        // 所有可拒绝检查及对象构造在前，下面在原双账号锁内消费双方票据。
                        foreach (var member in entered) member.Actor.Session.TransferPvpMatching(member);
                        foreach (var member in entered)
                        {
                            members.Add(member.Actor.Session, member);
                            matching.Remove(member.Actor.Session, load.Members.Single(ticket =>
                                ReferenceEquals(ticket.Actor.Session, member.Actor.Session)));
                        }
                        load.EnterRoom(entered);
                        rooms.Add(room.Id, room);
                        installed = true;
                        var view = room.View();
                        return new PvpMatchingRoomEntry(room, load.View(), entered.Select(member => Notice(member, view)).ToImmutableArray())
                            { Ratings = displayRatings.ToImmutable() };
                    }
                    finally { if (!installed) room.Commands.Release(); }
                }
            });
        }
        catch (InvalidOperationException) when (load.Members.Any(ticket => !ticket.Actor.Owner.IsCurrent)) { return null; }
    }

    // 调用方持原房间Commands；预加载屏障与普通战斗自身的加载/P2P屏障分别推进。
    public PvpArenaNotice[] StartOrdinaryMatching(PvpArenaRoom room, DateTimeOffset now)
    {
        var load = room.MatchingLoad;
        if (load is null) return [];
        try
        {
            return AccountOwners.Lease.ExecuteTogether(load.Members[0].Actor.Owner, load.Members[1].Actor.Owner, () =>
            {
                if (ratings is null) return [];
                lock (gate)
                    if (!Registered(load) || load.Phase != PvpMatchingLoadPhase.Ready || room.Match is not null) return [];
                var frozen = new Dictionary<ushort, PvpRatingSnapshot>();
                // 双方owner已按锁序持有；SQLite读取不能放进全服索引锁。
                foreach (var member in load.RoomMembers)
                {
                    if (!member.IsCurrent || !member.Actor.Session.RecoverPvpRatings(ratings)) return [];
                    if (load.Members.Single(ticket => ticket.Actor.Character.Id == member.Actor.Character.Id).TotalRoster is { } total)
                    {
                        if (totalRatings is null || !totalRatings.Recover(member.Actor.Owner)) return [];
                        foreach (var actor in total.Characters)
                            if (!ratings.Recover(member.Actor.Owner, actor.Id)) return [];
                        frozen.Add(member.Actor.Character.Id, totalRatings.Capture(member.Actor.Owner, total.Team.Id));
                    }
                    else frozen.Add(member.Actor.Character.Id, ratings.Capture(member.Actor.Owner, member.Actor.Character.Id));
                }
                lock (gate)
                {
                    if (!Registered(load) || load.Phase != PvpMatchingLoadPhase.Ready || room.Match is not null) return [];
                    foreach (var member in load.RoomMembers) room.SetReady(member.Token, true);
                    if (room.Start(room.Leader, now, TimeSpan.FromSeconds(30),
                        catalog.BattleDurationFor(load.Members[0].Actor.Session.ChannelEntry!.Type, room.IsTotal), frozen) is null) return [];
                    load.BeginBattle();
                    return Notices(room, room.View()).Select(notice => notice with { Kind = PvpArenaNoticeKind.Start }).ToArray();
                }
            });
        }
        catch (InvalidOperationException) when (load.Members.Any(ticket => !ticket.Actor.Owner.IsCurrent)) { return []; }
    }

    // 调用方持原房间Commands。先统一失效加载发布，再逐一清原Session，旧owner只拆原索引。
    public PvpArenaNotice[] CancelMatchingRoom(PvpArenaRoom room, DateTimeOffset now, bool expiredOnly = false)
    {
        PvpMatchingLoad load;
        bool preloading;
        lock (gate)
        {
            if (!Registered(room) || room.MatchingLoad is not { } current || room.Match is not null ||
                expiredOnly && (current.Phase != PvpMatchingLoadPhase.Loading || now < current.Deadline) ||
                current.Phase == PvpMatchingLoadPhase.Cancelled) return [];
            preloading = current.Phase is PvpMatchingLoadPhase.Loading or PvpMatchingLoadPhase.Ready;
            current.Cancel();
            load = current;
        }
        var departures = new List<PvpArenaNotice>();
        foreach (var member in load.RoomMembers)
        {
            bool removed = false;
            if (member.Actor.Owner.IsCurrent)
            {
                try
                {
                    member.Actor.Owner.Execute(() =>
                    {
                        lock (gate)
                        {
                            var departure = member.Actor.Session.LeavePvpArena(member);
                            departures.AddRange(Remove(member, departure).Where(notice => notice.Departure is not null));
                            removed = true;
                        }
                        return true;
                    });
                }
                catch (InvalidOperationException) when (!member.Actor.Owner.IsCurrent) { }
            }
            if (!removed) lock (gate) Remove(member, null);
        }
        return departures.Select(notice => preloading ? notice with { Kind = PvpArenaNoticeKind.MatchingCancelled } : notice).ToArray();
    }
}
