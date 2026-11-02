using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed partial class TownProjection
{
    internal LukeChannelCards? LukeChannelCards { get; set; }

    internal void PublishLukeChannelCards(GameConnectionContext context)
    {
        if (LukeChannelCards is not { } cards || !context.Owner.IsCurrent) return;
        if (context.Owner.Execute(() => context.LukeChannelCardsDisplay == (context.Session.SelectionEpoch, cards.CurrentDay))) return;
        var capture = context.Session.CaptureLukeChannelCards(cards);
        if (capture is null) return;
        var identity = (capture.Epoch, capture.Selection.Day);
        bool Current() => context.Session.CanPublishLukeChannelCards(capture, cards) &&
            context.LukeChannelCardsDisplay != identity;
        // 只记实际写出的频道预览；开战后由Battle冻结的同一日牌组接管。
        context.Sender.Post(0, (ushort)NotiPacketType.LUKE_RAID_CARD_DISPLAY,
            RaidCombatPackets.LukeCards(capture.Selection.Display), "luke_channel_cards", Current,
            written: () => context.LukeChannelCardsDisplay = identity);
    }
}
