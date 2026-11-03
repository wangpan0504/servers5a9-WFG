using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class ChronicleEnchantHandler(InventoryChronicleEnchantService service, InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.ENCHANT_3RD_CHRONICLE_ITEM, Handle, nameof(ChronicleEnchantHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool SameSelection() => context.Owner.Execute(() => ReferenceEquals(captured.Selected, context.Session.Selected) &&
            captured.SelectionEpoch == context.Session.SelectionEpoch && captured.Phase == context.Session.Phase);
        ChronicleEnchantRequest? request = null;
        try { request = ChronicleEnchantRequests.Read(packet.Body); }
        catch (InvalidDataException) { }
        ChronicleEnchantReceipt? receipt = null;
        bool resourceInvalid = false;
        var result = request is null || context.BlocksTownActions ? InventoryResult.Reject(InventoryChronicleEnchantService.Rejection) :
            context.Session.PlanAndCommitInventory(character =>
            {
                try { return service.Plan(character, request, out receipt); }
                catch (Exception error) when (error is InvalidDataException or FileNotFoundException or KeyNotFoundException or ArgumentException or OverflowException)
                { resourceInvalid = true; return InventoryResult.Reject(InventoryChronicleEnchantService.Rejection); }
            }, InventoryChronicleEnchantService.Rejection,
                effectItem: request.MaterialSlot >= 0 ? new(InventoryListType.Main, request.MaterialSlot) : null);
        bool Guard() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) : SameSelection();
        log.Write("chronicle_enchant", new { Connection = context.Id, request?.MaterialItemId, request?.MaterialSlot,
            request?.TargetItemId, request?.Target, request?.Number, result.Accepted, result.Error, receipt?.Succeeded,
            Reason = request is null ? "malformed" : resourceInvalid ? "resource_invalid" : result.Accepted ? "committed" : "rejected",
            Revision = result.Receipt?.Character.Revision });
        // 结果窗按目标槽读取装备；先发布实际已提交的Chronicle核心，不在打开结果窗后替换对象。
        if (result.Accepted && receipt is { Succeeded: true })
            await projection.RefreshAsync(context, result, new HashSet<InventorySlot> { new(InventoryListType.Main, receipt.MaterialSlot) },
                refreshEquipment: false, suppressMainNotice: true);
        await context.Reply(CmdPacketType.ENCHANT_3RD_CHRONICLE_ITEM, ChronicleEnchantPackets.Ack(result, result.Accepted ? receipt : null),
            result.Accepted ? "chronicle_enchant_committed" : "chronicle_enchant_rejected", Guard);
        if (result.Accepted && receipt is { Succeeded: false })
            await projection.RefreshAsync(context, result, new HashSet<InventorySlot>
                { new(InventoryListType.Main, receipt.MaterialSlot), new(InventoryListType.Main, receipt.TargetSlot) },
                refreshEquipment: false, suppressMainNotice: true);
        return CommandResult.Handled;
    }
}
