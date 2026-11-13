using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class EndlessAdventureProjection
{
    public static async Task PublishAsync(GameConnectionContext context, bool force = false, bool? eventCatalogActive = null)
    {
        if (!context.Session.NeedsEndlessAdventureRefresh && !(force && context.Session.EndlessAdventureActive)) return;
        if (context.Session.ReadEndlessAdventure() is not { } view) return;
        // 进度必须匹配同次108的开关快照；命令收尾与目录发送期间再次切换均交回原worker。
        if (eventCatalogActive is { } active ? active != view.Active :
            context.ServerEvents?.NeedsEndlessDisplay(view.Active) == true)
        {
            context.ServerEvents?.Refresh();
            return;
        }
        bool Current() => context.Session.CanPublishEndlessAdventure(view);
        await context.Notify(NotiPacketType.EVENT_VALUE_TO_CLIENT, EndlessAdventurePackets.Progress(view.Earned),
            "endless_adventure_progress", Current);
        if (Current() && !context.Sender.IsPublicationQueued) context.Session.PublishedEndlessAdventure(view);
    }
}
