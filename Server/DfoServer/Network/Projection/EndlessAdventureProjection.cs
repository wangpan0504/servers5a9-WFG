using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class EndlessAdventureProjection
{
    public static async Task PublishAsync(GameConnectionContext context, bool force = false, bool afterEventCatalog = false)
    {
        if (!context.Session.NeedsEndlessAdventureRefresh && !(force && context.Session.EndlessAdventureActive)) return;
        // 命令收尾或日切可能先于活动worker看到开关；先由原worker发布108，再接本次开关的1103。
        if (!afterEventCatalog && context.ServerEvents is { } events && events.NeedsEndlessDisplay(context.Session.EndlessAdventureActive))
        {
            events.Refresh();
            return;
        }
        if (context.Session.ReadEndlessAdventure() is not { } view) return;
        bool Current() => context.Session.CanPublishEndlessAdventure(view);
        await context.Notify(NotiPacketType.EVENT_VALUE_TO_CLIENT, EndlessAdventurePackets.Progress(view.Earned),
            "endless_adventure_progress", Current);
        if (Current() && !context.Sender.IsPublicationQueued) context.Session.PublishedEndlessAdventure(view);
    }
}
