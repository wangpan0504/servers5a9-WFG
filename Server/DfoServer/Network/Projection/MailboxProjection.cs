using DfoServer.Game.Mailbox;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class MailboxProjection(MailboxService service, OnlineSessionDirectory online)
{
    public async Task InitializeAsync(GameConnectionContext context)
    {
        var page = context.Session.OpenMailbox(service, initializing: true);
        await ListAsync(context, page.Page, page.Receipt, initializing: true);
    }
    public async Task ListAsync(GameConnectionContext context, MailboxPage page, MailboxReceipt receipt, bool initializing = false)
    {
        foreach (var body in MailboxPackets.List(page, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            await context.Notify(NotiPacketType.MAILBOX_MAIL_LIST, body, "mailbox_list", () => context.Session.CanPublishMailbox(receipt, initializing));
    }
    public void Alarm(GameConnectionContext context, MailboxReceipt receipt) => context.Sender.Post(0,
        (ushort)NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(), "mailbox_arrived", () => context.Session.CanPublishMailbox(receipt));
    public void Arrived(MailboxResult result)
    {
        if (result.Accepted && result.Recipient is { } recipient)
            online.Find(recipient.Account)?.NotifyMailbox(recipient.Id);
    }
    public static bool CanPublish(GameConnectionContext context, MailboxResult result) =>
        (result.Receipt is null || context.Session.CanPublishMailbox(result.Receipt)) &&
        (result.Inventory?.Receipt is null || context.Session.CanPublishInventory(result.Inventory.Receipt));

    public static async Task RestoreSendAsync(GameConnectionContext context, MailboxSendRequest request, MailboxResult result)
    {
        if (result.Receipt is null)
            return;
        var character = result.Receipt.Character;
        var slots = request.Items.Select(item => item.Type switch
        {
            0 or 2 => new InventorySlot(InventoryListType.Main, item.Slot),
            1 => new InventorySlot(InventoryListType.Avatar, item.Slot),
            3 or 7 => new InventorySlot(InventoryListType.Pet, item.Slot),
            _ => new InventorySlot(InventoryListType.Main, -1)
        }).Where(slot => slot.Index >= 0 && (slot.List == InventoryListType.Main && CurrencyPackets.VirtualSlots.Contains(slot.Index) ||
            Enum.GetValues<DfoServer.GameWorld.Items.ItemKind>().Any(kind =>
                DfoServer.Game.Inventory.Services.ItemSlotBoundService.Allows(character.Inventory, slot, kind))))
            .Append(new(InventoryListType.Main, 0)).Distinct().ToArray();
        bool CanSend() => CanPublish(context, result) && context.Session.Selected?.Revision == character.Revision;
        foreach (var list in slots.Select(slot => slot.List).Distinct())
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(character.Inventory, list, slots), "mail_send_restore", CanSend);
    }
}
