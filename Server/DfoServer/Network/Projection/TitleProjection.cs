using DfoServer.Game.Titles;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection.Inventory;

namespace DfoServer.Network.Projection;

internal sealed class TitleProjection(TitleAchievementService service)
{
    public void PrepareInitialization(GameConnectionContext context)
    {
        _ = context.Session.UpdateTitleAchievements(service, initializing: true);
    }
    public async Task InitializeAsync(GameConnectionContext context)
    {
        var character = context.Session.Selected ?? throw new InvalidOperationException("称号初始化缺少角色");
        await context.Notify(NotiPacketType.ACHIEVEMENT_LIST, TitlePackets.Progress(character), "title_progress_initialize");
        await BooksAsync(context, character.Inventory);
    }
    public async Task PublishAsync(GameConnectionContext context, TitleAchievementResult result, InventoryProjection inventory)
    {
        if (!result.Accepted || result.Plan is not { Changed: true } plan || result.Receipt is not { } receipt) return;
        bool CanSend() => context.Session.CanPublishTitle(receipt);
        foreach (int id in plan.Completed)
            await context.Notify(NotiPacketType.ACHIEVEMENT_COMPLETE, TitlePackets.Complete(id, service.Catalog, receipt.Character.Inventory), "title_achievement_complete", CanSend);
        await context.Notify(NotiPacketType.ACHIEVEMENT_LIST, TitlePackets.Progress(receipt.Character), "title_progress_committed", CanSend);
        await inventory.RefreshAsync(context, new(true, 0, plan.Inventory) { Receipt = receipt },
            plan.Inventory.ChangedSlots.Where(slot => TitleBookService.IsBook(slot.List)).ToHashSet());
        if (plan.Completed.Length != 0) await BooksAsync(context, receipt.Character.Inventory, CanSend);
        if (plan.Postals.Length != 0) await context.Notify(NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(), "title_reward_mail", CanSend);
    }
    public static async Task BooksAsync(GameConnectionContext context, DfoServer.Game.Inventory.Models.InventorySnapshot inventory, Func<bool>? canSend = null)
    {
        for (int category = 0; category < 5; category++)
            await context.Notify(NotiPacketType.TITLE_BOOK_LIST, TitlePackets.Book(inventory, category), "title_book_category", canSend);
    }
}
