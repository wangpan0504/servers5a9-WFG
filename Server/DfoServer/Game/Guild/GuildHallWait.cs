using DfoServer.Game.Session;
using DfoServer.GameWorld.Guilds;

namespace DfoServer.Game.Guild;

internal sealed record GuildHallWaitCredit(GuildDungeonMembership Membership, string EventId,
    long BusinessDay, int Periods);
internal sealed record GuildHallWaitCommit(GuildContributionCommit Contribution, int DailyPoints,
    bool MembershipCurrent = true);

internal interface IGuildHallWaitStore
{
    GuildDungeonMembership? ReadGuildHallWaitMembership(GuildManagementIdentity actor);
    GuildHallWaitCommit CreditGuildHallWait(GuildManagementIdentity actor, GuildHallWaitCredit credit,
        GuildHallWaitCatalog rules, long now);
}

// 每个会话仅保存当前连续停留区间；离厅和阶段变化由Session立即丢弃，不保存离线计时。
internal sealed class GuildHallWaitClock(GuildDungeonMembership membership, TimeProvider time)
{
    readonly string intervalId = Guid.NewGuid().ToString("N");
    long started = time.GetTimestamp();
    long day = GuildAttendanceDay.Start(time.GetUtcNow().ToUnixTimeSeconds());
    int consumed;
    int sequence;
    bool capped;
    GuildHallWaitCredit? pending;
    public GuildDungeonMembership Membership { get; } = membership;

    public GuildHallWaitCredit? Prepare(GuildHallWaitCatalog rules)
    {
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        long currentDay = GuildAttendanceDay.Start(now);
        long stamp = time.GetTimestamp();
        if (currentDay != day)
        {
            // 跨六点只保留已实际在线且属于新日的停留；旧日不足一周期不挪到新日。
            double carry = Math.Min(Math.Max(0, time.GetElapsedTime(started, stamp).TotalSeconds),
                Math.Max(0, now - currentDay));
            started = checked(stamp - (long)(carry * time.TimestampFrequency));
            day = currentDay;
            consumed = 0;
            capped = false;
            pending = null;
        }
        if (capped) return null;
        if (pending is not null) return pending;
        double elapsed = Math.Max(0, time.GetElapsedTime(started, stamp).TotalSeconds);
        int periods = (int)Math.Min(rules.DailyMaximum / (long)rules.Points + 1,
            Math.Floor(elapsed / rules.IntervalSeconds) - consumed);
        if (periods <= 0) return null;
        pending = new(Membership, $"hall:{Membership.CharacterId}:{intervalId}:{++sequence}", day, periods);
        return pending;
    }

    public void Complete(GuildHallWaitCredit credit, GuildHallWaitCommit result, GuildHallWaitCatalog rules)
    {
        if (!ReferenceEquals(pending, credit) || !result.Contribution.Success || !result.MembershipCurrent) return;
        consumed = checked(consumed + credit.Periods);
        capped = result.DailyPoints >= rules.DailyMaximum;
        pending = null;
    }
}

// 一个有界生命周期循环复用在线目录；不为每个玩家建立Timer或另一套在线字典。
internal sealed class GuildHallWaitService(IGuildHallWaitStore store, GuildHallWaitCatalog catalog,
    OnlineSessionDirectory online, GuildContributionService contribution, TimeProvider? clock = null,
    Action<Exception>? failure = null, GuildRankingService? ranking = null,
    Action<GameSession, ushort, DateTimeOffset>? contributionCommitted = null)
{
    readonly TimeProvider time = clock ?? TimeProvider.System;

    public void Sweep()
    {
        foreach (var actor in online.Snapshot())
        {
            if (!actor.Ready || actor.Phase != SessionPhase.Town) continue;
            try
            {
                var session = online.Find(actor.Account);
                var result = session?.TickGuildHallWait(store, catalog, time);
                // 跨账号通知在本人的owner释放后发布，不在计时/SQL锁内取其他账号锁。
                if (result is { Success: true, Points: > 0 } && session is not null)
                {
                    contribution.Publish(result);
                    contributionCommitted?.Invoke(session, actor.CharacterId, time.GetUtcNow());
                }
            }
            catch (Exception error) when (failure is not null)
            {
                failure(error);
            }
        }
        try { ranking?.Tick(); }
        catch (Exception error) when (failure is not null) { failure(error); }
    }

    public async Task RunAsync(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        while (await timer.WaitForNextTickAsync(cancellation)) Sweep();
    }
}
