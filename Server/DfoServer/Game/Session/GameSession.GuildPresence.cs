using DfoServer.Game.Guild;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public Action<GuildPresenceFailure>? GuildPresenceFailed { get; set; }
    public GuildPresenceFailure? LastGuildPresenceFailure { get; private set; }

    void ObserveGuildAttendance()
    {
        if (!owner.IsCurrent || !Ready || Selected is not { } character || repository is not IGuildAttendanceStore store) return;
        long now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        try { store.ObserveGuildAttendance(owner, character.Id, now); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var failure = new GuildPresenceFailure(character.Id, now, error);
            LastGuildPresenceFailure = failure;
            try { GuildPresenceFailed?.Invoke(failure); }
            catch (Exception reporting) when (reporting is not OutOfMemoryException)
            { LastGuildPresenceFailure = failure with { Error = new AggregateException(error, reporting) }; }
        }
    }

    void ObserveGuildPresence()
    {
        if (!owner.IsCurrent || !Ready || Selected is not { } character || repository is not IGuildPresenceStore store)
            return;
        long observedAt = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        try
        {
            store.ObserveGuildMemberOnline(owner, character.Id, observedAt);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // 在线历史是辅助资料：保存失败仍须完成入城或断开，并向宿主保留可观测的失败。
            var failure = new GuildPresenceFailure(character.Id, observedAt, error);
            LastGuildPresenceFailure = failure;
            try
            {
                GuildPresenceFailed?.Invoke(failure);
            }
            catch (Exception reportingError) when (reportingError is not OutOfMemoryException)
            {
                LastGuildPresenceFailure = failure with { Error = new AggregateException(error, reportingError) };
            }
        }
    }
}
