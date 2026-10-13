using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class DungeonCardMembershipProjection
{
    public static async Task RefreshAsync(GameConnectionContext context)
    {
        var receipt = context.Session.CardMembershipForClient();
        if (receipt is null) return;
        bool CanSend() => context.Session.CanPublishCardMembership(receipt);
        var now = DateTimeOffset.UtcNow;
        await context.Notify(NotiPacketType.PREMIUM_INFO_LIST,
            PremiumPackets.SelectEntries(receipt.Character.Inventory.AccountWallet, now, [56, 58]), "premium_info_committed", CanSend);
        await context.Reply(CmdPacketType.PREMIUM_SERVICE,
            DungeonCardMembershipPackets.DevilService0(receipt.Membership, now.ToUnixTimeSeconds()), "card_devil_service0_state", CanSend);
    }
}
