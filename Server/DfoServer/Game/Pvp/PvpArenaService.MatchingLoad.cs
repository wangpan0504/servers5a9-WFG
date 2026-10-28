using DfoServer.Game.Accounts;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Pvp;

namespace DfoServer.Game.Pvp;

internal sealed partial class PvpArenaService
{
    uint nextMatchingLoad;

    // 只在容量有界的当前频道快照中按原排队顺序找候选；竞争失败不移除或重排未配成者。
    public PvpMatchingLoadView? PairNextOrdinary(PvpMatchingTicket requester, DateTimeOffset now, TimeSpan timeout)
    {
        foreach (var candidate in QueuedMatching(requester.Identity.Channel))
        {
            if (ReferenceEquals(candidate, requester) || !candidate.Preferences.Compatible(requester.Preferences)) continue;
            if (PairOrdinary(candidate, requester, now, timeout) is { } load) return load;
        }
        return null;
    }

    // 候选策略在外层决定；这里仅把两份精确等待票据原子变为同一预约。
    // 必须在未持单账号锁时调用，不能从某一方owner升级到双账号锁。
    public PvpMatchingLoadView? PairOrdinary(PvpMatchingTicket first, PvpMatchingTicket second,
        DateTimeOffset now, TimeSpan timeout)
    {
        if (ReferenceEquals(first, second) || first.Actor.Owner.Account == second.Actor.Owner.Account ||
            first.IsTotal != second.IsTotal ||
            first.Identity.Channel != second.Identity.Channel || !first.Preferences.Compatible(second.Preferences) ||
            timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            return null;
        // 资源/公开信息捕获不占全服索引锁；锁内只复核原角色快照和资格。
        try
        {
            var firstCharacter = first.Actor.Session.PublicCharacter(first.Actor.Character.Id);
            var secondCharacter = second.Actor.Session.PublicCharacter(second.Actor.Character.Id);
            if (firstCharacter is null || secondCharacter is null) return null;
            return AccountOwners.Lease.ExecuteTogether(first.Actor.Owner, second.Actor.Owner, () =>
            {
                if (!CurrentTotalRoster(first) || !CurrentTotalRoster(second)) return null;
                lock (gate)
                {
                    if (!Waiting(first) || !Waiting(second) || nextMatchingLoad == uint.MaxValue ||
                        !firstCharacter.IsUnchanged() || !secondCharacter.IsUnchanged() ||
                        Excluded(first, second, secondCharacter.Character) || Excluded(second, first, firstCharacter.Character) ||
                        !catalog.CanEnter(PvpArenaEligibility.Matching, firstCharacter.Character.Job,
                            (byte)(firstCharacter.Character.GrowType & 15)) ||
                        !catalog.CanEnter(PvpArenaEligibility.Matching, secondCharacter.Character.Job,
                            (byte)(secondCharacter.Character.GrowType & 15))) return null;
                    var load = new PvpMatchingLoad(++nextMatchingLoad, first, second, now + timeout,
                        [firstCharacter, secondCharacter]);
                    first.Bind(load);
                    second.Bind(load);
                    return load.View();
                }
            });
        }
        catch (InvalidOperationException) when (!first.Actor.Owner.IsCurrent || !second.Actor.Owner.IsCurrent) { return null; }
    }

    public PvpMatchingLoadView? CompleteMatchingLoad(GameSession session, uint id, DateTimeOffset now)
    {
        var load = session.CurrentPvpMatching?.Load ?? session.CurrentPvpArena?.Room.MatchingLoad;
        var ticket = load?.Members.FirstOrDefault(row => ReferenceEquals(row.Actor.Session, session));
        if (load is null || ticket is null || load.Id != id) return null;
        try
        {
            return AccountOwners.Lease.ExecuteTogether(load.Members[0].Actor.Owner, load.Members[1].Actor.Owner, () =>
            {
                lock (gate)
                {
                    if (!Registered(load) || !load.Complete(ticket, now)) return null;
                    return load.View();
                }
            });
        }
        catch (InvalidOperationException) when (load.Members.Any(row => !row.Actor.Owner.IsCurrent)) { return null; }
    }

    public bool ExpireMatchingLoad(PvpMatchingLoadView expected, DateTimeOffset now)
    {
        var load = expected.Members.FirstOrDefault()?.Load;
        return load is not null && load.Id == expected.Id && ReleaseMatchingLoad(load,
            current => current.Phase == PvpMatchingLoadPhase.Loading && now >= current.Deadline);
    }

    bool CancelMatchingLoad(GameSession session, PvpMatchingTicket ticket, PvpMatchingLoad load)
    {
        try
        {
            // 只校验调用者拥有这份票据，清理在无账号锁时按原双方引用执行。
            if (!ticket.Actor.Owner.Execute(() => ReferenceEquals(session.CurrentPvpMatching, ticket) &&
                ReferenceEquals(ticket.Load, load))) return false;
        }
        catch (InvalidOperationException) when (!ticket.Actor.Owner.IsCurrent) { return false; }
        return ReleaseMatchingLoad(load);
    }

    bool Waiting(PvpMatchingTicket ticket) => ticket.Load is null && ticket.IsCurrent &&
        ticket.Actor.Session.CanRemainPvpMatching && matching.TryGetValue(ticket.Actor.Session, out var current) &&
        ReferenceEquals(current, ticket);

    bool CurrentTotalRoster(PvpMatchingTicket ticket)
    {
        if (ticket.TotalRoster is not { } frozen) return true;
        if (totalTeams?.Read(ticket.Actor.Owner) is not { } team || !team.SameLineup(frozen.Team) || totalSkills is null ||
            ticket.Actor.Session.CapturePvpTotalBattleRoster(team, frozen.Selection, totalSkills, matching: true) is not { } current) return false;
        return current.Characters.Zip(frozen.Characters).All(pair => pair.First.Id == pair.Second.Id &&
            pair.First.Revision == pair.Second.Revision);
    }

    static bool Excluded(PvpMatchingTicket requester, PvpMatchingTicket opponent, DfoServer.Game.Characters.Character representative) =>
        opponent.TotalRoster is { } total ? total.Characters.Any(requester.Preferences.Excludes) : requester.Preferences.Excludes(representative);

    bool Registered(PvpMatchingLoad load) => load.IsCurrent && (load.Room is { } room
        ? Registered(room) && load.RoomMembers.All(Current)
        : load.Members.All(ticket => matching.TryGetValue(ticket.Actor.Session, out var current) && ReferenceEquals(current, ticket)));

    // 先使原预约不可发布并拆索引，再逐一在有效owner下清Session；不嵌套账号锁。
    // 接管者或重新排队者不持原引用，旧预约收尾不会清除其新票据。
    bool ReleaseMatchingLoad(PvpMatchingLoad load, Func<PvpMatchingLoad, bool>? condition = null)
    {
        lock (gate)
        {
            // 入房后原票据已消费，旧排队取消/超时不能越过房间门禁拆新成员。
            if (load.Room is not null || condition?.Invoke(load) == false || !load.Cancel()) return false;
            foreach (var ticket in load.Members) matching.Remove(ticket.Actor.Session, ticket);
        }
        foreach (var ticket in load.Members)
        {
            try { ticket.Actor.Owner.Execute(() => ticket.Actor.Session.RemovePvpMatching(ticket)); }
            catch (InvalidOperationException) when (!ticket.Actor.Owner.IsCurrent) { }
        }
        return true;
    }

    void SweepMatching(DateTimeOffset? time = null)
    {
        var now = time ?? DateTimeOffset.UtcNow;
        PvpMatchingLoad[] obsolete;
        lock (gate)
        {
            obsolete = matching.SnapshotValues().Select(row => row.Load).OfType<PvpMatchingLoad>().Distinct()
                .Where(load => !load.IsCurrent || load.Phase == PvpMatchingLoadPhase.Loading && now >= load.Deadline)
                .ToArray();
        }
        foreach (var load in obsolete) ReleaseMatchingLoad(load, current => !current.IsCurrent ||
            current.Phase == PvpMatchingLoadPhase.Loading && now >= current.Deadline);
    }
}
