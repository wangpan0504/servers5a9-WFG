using DfoServer.Game.Social;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public Action<SocialPresenceFailure>? SocialPresenceFailed { get; set; }
    public SocialPresenceFailure? LastSocialPresenceFailure { get; private set; }

    void ObserveSocialPresence()
    {
        if (!owner.IsCurrent || !Ready || Selected is null || repository is not ISocialPresenceStore store)
            return;
        long observedAt = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        try
        {
            store.ObserveSocialAccountOnline(owner, observedAt);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var failure = new SocialPresenceFailure(owner.Account, observedAt, error);
            LastSocialPresenceFailure = failure;
            try { SocialPresenceFailed?.Invoke(failure); }
            catch (Exception reporting) when (reporting is not OutOfMemoryException)
            { LastSocialPresenceFailure = failure with { Error = new AggregateException(error, reporting) }; }
        }
    }
}
