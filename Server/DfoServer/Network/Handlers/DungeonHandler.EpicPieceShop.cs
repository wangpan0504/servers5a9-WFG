using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler
{
    async Task PublishEpicPieceShopAsync(GameConnectionContext context, bool prepare)
    {
        if (epicPieceShop is null || context.Session.EpicPieceShopPresentation(epicPieceShop, prepare) is not { } view) return;
        bool sent = false;
        await context.Notify(NotiPacketType.EPIC_PIECE_EXCHANGE_NPC, [view.Visible ? (byte)1 : (byte)0], "epic_piece_shop_npc",
            () => sent = context.Session.CanPublishFormalDungeon(view.Scene));
        if (sent) context.Session.ConfirmEpicPieceShopPresentation(view.Scene);
        log.Write("epic_piece_shop", new { Connection = context.Id, Action = "appearance", view.Visible, Sent = sent,
            Npc = epicPieceShop.Rules.NpcId, view.Scene.Entry.DungeonId });
    }
}
