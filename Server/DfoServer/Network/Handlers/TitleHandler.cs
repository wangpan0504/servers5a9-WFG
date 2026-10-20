using DfoServer.Game.Titles;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class TitleHandler(TitleAchievementService achievements, TitleBookService books, TitleProjection titles,
    InventoryProjection inventory, DfoServer.Game.Session.OnlineSessionDirectory online) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.ACHIEVEMENT_TRIGGER, Trigger, nameof(TitleHandler));
        commands.Register(CmdPacketType.TITLE_BOOK_PUT, Put, nameof(TitleHandler));
        commands.Register(CmdPacketType.TITLE_BOOK_GET, Get, nameof(TitleHandler));
        commands.Register(CmdPacketType.OTHER_USER_TITLE_BOOK_LIST, Other, nameof(TitleHandler));
    }
    async Task<CommandResult> Other(GameConnectionContext context, Packet packet)
    {
        ushort target = TitleRequests.Other(packet.Body);
        var current = context.Session.Selected;
        long epoch = context.Session.SelectionEpoch;
        if (current is null || !context.Session.Ready || !online.HasReadyCharacter(target))
            return CommandResult.Handled;
        var book = context.Session.ReadPublicTitleBook(target);
        if (book is null) return CommandResult.Handled;
        for (int category = 0; category < 5; category++)
            await context.Notify(NotiPacketType.TITLE_BOOK_LIST, TitlePackets.Book(book, category, target, show: true), "other_title_book",
                () => context.Session.Ready && context.Session.SelectionEpoch == epoch && context.Session.Selected?.Id == current.Id);
        return CommandResult.Handled;
    }
    async Task<CommandResult> Trigger(GameConnectionContext context, Packet packet)
    {
        var request = TitleRequests.Trigger(packet.Body);
        var result = context.Session.UpdateTitleAchievements(achievements, request);
        await context.Reply(CmdPacketType.ACHIEVEMENT_TRIGGER, TitlePackets.Trigger(result), "title_achievement_trigger",
            result.Receipt is { } receipt ? () => context.Session.CanPublishTitle(receipt) : null);
        await titles.PublishAsync(context, result, inventory);
        return CommandResult.Handled;
    }
    Task<CommandResult> Put(GameConnectionContext context, Packet packet) => Transfer(context, packet, put: true);
    Task<CommandResult> Get(GameConnectionContext context, Packet packet) => Transfer(context, packet, put: false);
    async Task<CommandResult> Transfer(GameConnectionContext context, Packet packet, bool put)
    {
        var request = TitleRequests.Book(packet.Body);
        var result = context.Session.PlanAndCommitInventory(character => put ? books.Put(character, request, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) :
            books.Get(character, request, DateTimeOffset.UtcNow.ToUnixTimeSeconds()), 2, requireTown: false);
        var opcode = put ? CmdPacketType.TITLE_BOOK_PUT : CmdPacketType.TITLE_BOOK_GET;
        // ACK先移动已有物品对象；源槽不能提前清空，之后再投影本人及同屏的实际穿戴。
        await context.Reply(opcode, TitlePackets.BookAck(request, result), put ? "title_book_put" : "title_book_get",
            result.Receipt is { } receipt ? () => context.Session.CanPublishInventory(receipt) : null);
        if (result.Accepted && result.Receipt is { } committed)
        {
            var acknowledged = result.Plan!.ChangedSlots.Where(slot => TitleBookService.IsBook(slot.List) || slot.List == InventoryListType.Equipment).ToHashSet();
            await inventory.RefreshAsync(context, result, acknowledged);
            await TitleProjection.BooksAsync(context, committed.Character.Inventory, () => context.Session.CanPublishInventory(committed));
        }
        return CommandResult.Handled;
    }
}
