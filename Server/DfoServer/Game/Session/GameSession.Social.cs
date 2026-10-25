using DfoServer.Game.Social;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public Action<SocialView>? SocialNoticeArrived { get; set; }
    public Action<string, Func<bool>>? SocialAccountDeleted { get; set; }

    public SocialActor? CaptureSocialActor()
    {
        try
        {
            return owner.Execute(() => Ready && Selected is { } character
                ? new SocialActor(owner, this, character, SelectionEpoch) : null);
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }
}
