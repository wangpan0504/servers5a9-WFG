using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class StackableActionHandler
{
    async Task<CommandResult> UseStoryBookAsync(GameConnectionContext context, StackableActionUseRequest request)
    {
        var rejectedGuard = CaptureAuraRejectionGuard(context);
        InventoryResult result = request.List != (byte)InventoryListType.Main || request.Slot > short.MaxValue || request.Reserved != 0 ||
            request.FirstArgument is 0 or >= 30000 || request.SecondArgument != 0 || context.Activity?.BlocksTownActions == true
            ? InventoryResult.Reject(0) with { RejectReason = "invalid-story-book-request" }
            : context.Session.UseStoryBook(new(InventoryListType.Main, (short)request.Slot),
                (ushort)request.FirstArgument, quests, items);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : rejectedGuard();
        log.Write("consumable_use", new { Connection = context.Id, Opcode = 515, request.Slot, request.List, request.Action,
            Item = result.Plan?.Before.Find(new(InventoryListType.Main, (short)request.Slot))?.Core.ItemId,
            QuestId = request.FirstArgument, result.Accepted, result.Error, Reason = result.RejectReason ?? (result.Accepted ? "committed" : "rejected"),
            Character = result.Receipt?.Character.Id, Revision = result.Receipt?.Character.Revision });
        // 028C0AE0/action139自行扣一张并询问是否开始；确认回调028BF6A0→02280790才发送31。
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION, result.Accepted ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request),
            result.Accepted ? "story_book_used" : "story_book_rejected", Guard);
        if (result.Receipt is { } committed)
        {
            await (inventoryProjection ?? throw new InvalidOperationException("故事簿消费缺少库存投影"))
                .RefreshAsync(context, result, new HashSet<InventorySlot> { new(InventoryListType.Main, (short)request.Slot) });
            await projection.RefreshAsync(context, committed.Character, Guard, restoreCompleted: false);
            await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(committed.Character), "story_book_history", Guard);
        }
        return CommandResult.Handled;
    }
}
