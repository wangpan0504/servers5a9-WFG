using DfoServer.Game.EpicWish;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class EpicWishHandler(EpicWishService service) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.UPDATE_WISH_ITEM, Update, nameof(EpicWishHandler));

    async Task<CommandResult> Update(GameConnectionContext context, Packet packet)
    {
        EpicWishReceipt? receipt;
        try
        {
            receipt = context.Session.SaveEpicWish(service, EpicWishRequests.Update(packet.Body));
        }
        catch (InvalidDataException)
        {
            receipt = null;
        }

        if (receipt is not null)
            await context.Notify(NotiPacketType.WISH_ITEM_INFO, EpicWishPackets.Info(receipt.Character.EpicWishes),
                "epic_wish_saved", () => context.Session.CanPublishEpicWish(receipt));
        await context.Reply(CmdPacketType.UPDATE_WISH_ITEM, EpicWishPackets.Updated(receipt is not null),
            "epic_wish_update", receipt is null ? null : () => context.Session.CanPublishEpicWish(receipt));
        return CommandResult.Handled;
    }
}
