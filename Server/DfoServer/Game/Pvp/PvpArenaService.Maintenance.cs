namespace DfoServer.Game.Pvp;

internal sealed partial class PvpArenaService
{
    // 回收预加载并消费已提交的普通局来源；正式局超时不能借维护扫描猜HP。
    public async Task<PvpArenaNotice[]> MaintainMatchingAsync(DateTimeOffset now, CancellationToken stop = default,
        Action<ushort, Exception>? failed = null)
    {
        stop.ThrowIfCancellationRequested();
        SweepMatching(now);
        PvpArenaRoom[] pending;
        lock (gate)
        {
            PruneMatching();
            pending = rooms.SnapshotValues().Where(room => NeedsCleanup(room) || room.Match?.HasExpiredClock(now) == true ||
                room.Match?.HasExpiredReturn(now) == true || NeedsRecord(room) || NeedsRating(room) || NeedsResult(room)).ToArray();
        }
        var notices = new List<PvpArenaNotice>();
        foreach (var room in pending)
        {
            // 繁忙房间留到下次，不能让一个房间阻塞所有预加载期限。
            if (!await room.Commands.WaitAsync(0, stop)) continue;
            try
            {
                bool cleanup;
                lock (gate) cleanup = Registered(room) && NeedsCleanup(room);
                if (cleanup) notices.AddRange(CancelMatchingRoom(room, now));
                lock (gate)
                    if (Registered(room) && room.Match?.ExpireClock(now) == true && room.IsTotal)
                        notices.AddRange(Notices(room, room.View()).Select(notice => notice with { Kind = PvpArenaNoticeKind.TotalProgress }));
                {
                    PvpArenaMembership[] recipients;
                    lock (gate) recipients = Registered(room) && (NeedsRecord(room) || NeedsRating(room))
                        ? members.SnapshotValues().Where(member => ReferenceEquals(member.Room, room) && member.IsCurrent &&
                            room.Match!.Fighters.Any(fighter => fighter.Membership == member.Token) &&
                            !room.Match.HasRatingReceipt(member.Token)).ToArray() : [];
                    foreach (var member in recipients)
                    {
                        stop.ThrowIfCancellationRequested();
                        try { RecordResult(member.Actor.Session, now); }
                        catch (InvalidOperationException) when (!member.IsCurrent) { }
                    }
                }
                PublishResults(room, now);
                bool returned;
                lock (gate) returned = Registered(room) && room.ExpireResult(now);
                if (returned)
                {
                    if (room.IsMatching) notices.AddRange(CancelMatchingRoom(room, now));
                    else lock (gate) notices.AddRange(Notices(room, room.View()).Select(notice => notice with
                        { Kind = PvpArenaNoticeKind.ResultReturned, Characters = [] }));
                }
            }
            catch (System.Data.Common.DbException exception) when (failed is not null) { failed(room.Id, exception); }
            finally { room.Commands.Release(); }
        }
        return notices.ToArray();

        bool NeedsCleanup(PvpArenaRoom room) => room.Match is null && room.MatchingLoad is { } load &&
            (!load.IsCurrent || load.Phase == PvpMatchingLoadPhase.Loading && now >= load.Deadline);
        bool NeedsRecord(PvpArenaRoom room) => room.Match is { Result: not null, CommittedRecord: null };
        bool NeedsRating(PvpArenaRoom room) => ratings is not null && room.Match is
            { CommittedRecord: not null } match &&
            match.Fighters.Any(fighter => room.Member(fighter.Membership) is not null && !match.HasRatingReceipt(fighter.Membership));
        bool NeedsResult(PvpArenaRoom room) => ratings is not null && room.Match is
            { Phase: PvpArenaMatchPhase.Finished or PvpArenaMatchPhase.Cancelled, CommittedRecord: not null } match &&
            match.Fighters.Any(fighter => room.Member(fighter.Membership) is not null && !match.IsResultQueued(fighter.Membership));
    }
}
