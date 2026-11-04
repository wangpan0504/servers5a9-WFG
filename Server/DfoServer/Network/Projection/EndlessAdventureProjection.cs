using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class EndlessAdventureProjection
{
    public static async Task PublishAsync(GameConnectionContext context, bool force = false)
    {
        if (!context.Session.NeedsEndlessAdventureRefresh && !(force && context.Session.EndlessAdventureActive)) return;
        if (context.Session.ReadEndlessAdventure() is not { } view) return;
        bool Current() => context.Session.CanPublishEndlessAdventure(view);
        await context.Notify(NotiPacketType.EVENT_VALUE_TO_CLIENT, EndlessAdventurePackets.Progress(view.Earned),
            "endless_adventure_progress", Current);
        if (Current() && !context.Sender.IsPublicationQueued) context.Session.PublishedEndlessAdventure(view);
    }
}
