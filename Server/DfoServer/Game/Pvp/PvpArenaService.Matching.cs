using DfoServer.Game.Caching;
using DfoServer.Game.Session;
using DfoServer.Game.Social;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Pvp;

namespace DfoServer.Game.Pvp;

internal sealed record PvpMatchingTicket(Guid Token, SocialActor Actor, PublicCharacterSnapshot Identity, long Order)
{
    public PvpMatchingPreferences Preferences { get; init; } = PvpMatchingPreferences.Default;
    public PvpTotalBattleRoster? TotalRoster { get; init; }
    public bool IsTotal => TotalRoster is not null;
    PvpMatchingLoad? load;
    public PvpMatchingLoad? Load => Volatile.Read(ref load);
    internal void Bind(PvpMatchingLoad value) => Volatile.Write(ref load, value);
    public bool IsCurrent => Actor.IsCurrent() && Actor.Session.Phase == SessionPhase.Town &&
        ReferenceEquals(Actor.Session.CurrentPvpMatching, this);
}

internal sealed partial class PvpArenaService
{
    readonly GameCacheIndex<GameSession, PvpMatchingTicket> matching;
    readonly int matchingCapacity;
    long matchingOrder;

    public PvpMatchingTicket? QueueOrdinary(GameSession session, bool blocked = false) =>
        QueueOrdinary(session, PvpMatchingPreferences.Default, blocked);

    // 网络模式准入独立处理；保存已核验的原偏好，不在重复排队时静默修改旧票据。
    public PvpMatchingTicket? QueueOrdinary(GameSession session, PvpMatchingPreferences preferences, bool blocked = false)
        => QueueMatching(session, preferences, null, blocked);

    public PvpMatchingTicket? QueueTotal(GameSession session, PvpMatchingPreferences preferences, bool blocked = false)
    {
        if (blocked || !preferences.IsValid || totalTeams is null || totalRatings is null || totalSkills is null || ratings is null) return null;
        if (session.CurrentPvpMatching is { } existing)
            return existing.Actor.Execute(() => existing.IsTotal && existing.IsCurrent && existing.Preferences.SameChoice(preferences) &&
                CurrentTotalRoster(existing) ? existing : null);
        if (
            totalTeams.CaptureInitialization(session) is not { } initialization) return null;
        var captured = initialization.Selection.Actor.Execute(() =>
        {
            if (!RecoverRatings(session) || !totalRatings.Recover(initialization.Selection.Actor.Owner)) return null;
            foreach (var member in initialization.Selection.Members)
                if (!ratings.Recover(initialization.Selection.Actor.Owner, member.Character.Id)) return null;
            var roster = session.CapturePvpTotalBattleRoster(initialization.Team, initialization.Selection, totalSkills);
            if (roster is null || roster.Characters.Any(actor => !catalog.CanEnter(PvpArenaEligibility.Matching,
                actor.Job, (byte)(actor.GrowType & 15))) || roster.Characters.Select(actor => (actor.Job, Grow: actor.GrowType & 15))
                .Distinct().Count() != 3) return null;
            return roster;
        });
        return captured is null ? null : QueueMatching(session, preferences, captured, false);
    }

    PvpMatchingTicket? QueueMatching(GameSession session, PvpMatchingPreferences preferences,
        PvpTotalBattleRoster? totalRoster, bool blocked)
    {
        if (blocked || !preferences.IsValid) return null;
        SweepMatching();
        var admission = session.CapturePvpMatchingAdmission();
        if (admission is null || !catalog.CanEnter(PvpArenaEligibility.Matching,
            admission.Actor.Character.Job, (byte)(admission.Actor.Character.GrowType & 15))) return null;
        return admission.Actor.Execute(() =>
        {
            if (!RecoverRatings(session)) return null;
            if (totalRoster is not null && (totalSkills is null || !session.CanUsePvpTotalRoster(totalRoster.Selection))) return null;
            lock (gate)
            {
                PruneMatching();
                if (!admission.Actor.IsCurrent() || !ReferenceEquals(session.Selected, admission.Actor.Character)) return null;
                if (matching.TryGetValue(session, out var existing) && existing.IsCurrent)
                    return existing.IsTotal == (totalRoster is not null) && existing.Preferences.SameChoice(preferences) ? existing : null;
                if (matching.Count >= matchingCapacity) return null;
                if (session.CurrentPvpMatching is { } stale) session.RemovePvpMatching(stale);
                var ticket = new PvpMatchingTicket(Guid.NewGuid(), admission.Actor, admission.Identity, ++matchingOrder)
                    { Preferences = preferences, TotalRoster = totalRoster };
                if (!session.InstallPvpMatching(ticket)) return null;
                matching.Add(session, ticket);
                return ticket;
            }
        });
    }

    public bool CancelMatching(GameSession session, PvpMatchingTicket expected)
    {
        if (!ReferenceEquals(expected.Actor.Session, session)) return false;
        if (expected.Load is { } load) return CancelMatchingLoad(session, expected, load);
        bool paired = false;
        bool removed;
        try { removed = expected.Actor.Owner.Execute(Cancel); }
        catch (InvalidOperationException) when (!expected.Actor.Owner.IsCurrent) { return false; }
        // 捕获与取得本人owner之间允许配对，必须退出单账号锁后再清理双方原票据。
        return paired ? CancelMatchingLoad(session, expected, expected.Load!) : removed;

        bool Cancel()
        {
            lock (gate)
            {
                if (expected.Load is not null) { paired = true; return false; }
                if (!session.RemovePvpMatching(expected)) return false;
                matching.Remove(session, expected);
                return true;
            }
        }
    }

    public PvpMatchingTicket[] QueuedMatching(ChannelIdentity channel)
    {
        SweepMatching();
        lock (gate)
        {
            PruneMatching();
            return matching.SnapshotValues().Where(row => row.Load is null && row.Identity.Channel == channel)
                .OrderBy(row => row.Order).ToArray();
        }
    }

    // 收尾不借失效owner写任何新状态；索引按原Session定位，不能移除接管者或重排的新票据。
    void DetachMatching(GameSession session)
    {
        PvpMatchingTicket? ticket;
        lock (gate) { if (!matching.TryGetValue(session, out ticket)) return; }
        if (ticket.Load is { } load) { ReleaseMatchingLoad(load); return; }
        if (ticket.Actor.Owner.IsCurrent)
        {
            try { CancelMatching(session, ticket); }
            catch (InvalidOperationException) when (!ticket.Actor.Owner.IsCurrent) { }
        }
        lock (gate) matching.Remove(session, ticket);
    }

    void PruneMatching()
    {
        foreach (var ticket in matching.SnapshotValues())
            if (!ticket.IsCurrent) matching.Remove(ticket.Actor.Session, ticket);
    }
}
