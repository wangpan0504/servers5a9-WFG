using DfoServer.Game.Rental;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers;

// S5A9 租赁请求只负责解析、提交后应答和投影；费用与装备实例由 RentalService 规划。
internal sealed class RentalHandler(RentalService rental, InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public async Task<CommandResult> UsePointsAsync(GameConnectionContext context, StackableActionUseRequest request)
    {
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool valid = request.Action == 106 && request.Reserved == 0 && request.FirstArgument == 0 && request.SecondArgument == 0 &&
            request.List == 0 && request.Slot <= short.MaxValue;
        var position = new InventorySlot(InventoryListType.Main, unchecked((short)request.Slot));
        var result = !valid || context.BlocksTownActions ? InventoryResult.Reject(19) :
            context.Session.PlanAndCommitInventory(character => rental.UsePoints(character, position), 19);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            ReferenceEquals(context.Session.Selected, before.Selected) && context.Session.SelectionEpoch == before.SelectionEpoch &&
            context.Session.Phase == before.Phase);
        log.Write("rental_point_item", new { Connection = context.Id, request.Slot, result.Accepted, result.Error });
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
            result.Accepted ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request, result.Error),
            result.Accepted ? "rental_point_item_used" : "rental_point_item_rejected", Guard);
        await PublishStateAsync(context, result, "rental_state_after_item");
        await projection.RefreshAsync(context, result, new HashSet<InventorySlot> { position }, refreshEquipment: false);
        return CommandResult.Handled;
    }

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.CHARGE_RENTPOINT, ChargeAsync, nameof(RentalHandler));
        commands.Register(CmdPacketType.RENT_EQUIPMENT_ITEM, RentAsync, nameof(RentalHandler));
    }

    async Task<CommandResult> ChargeAsync(GameConnectionContext context, Packet packet)
    {
        RentalChargeRequest request;
        try { request = RentalRequests.Charge(packet.Body); }
        catch (InvalidDataException ex)
        {
            log.Write("rental_charge_rejected", new { Connection = context.Id, Reason = ex.Message });
            await context.Reply(CmdPacketType.CHARGE_RENTPOINT, RentalPackets.ChargeFailure(), "rental_charge_malformed");
            return CommandResult.Handled;
        }

        InventoryResult result = context.BlocksTownActions ? InventoryResult.Reject() :
            context.Session.PlanAndCommitInventory(character => rental.Charge(character, request.Count));
        await context.Reply(CmdPacketType.CHARGE_RENTPOINT,
            result.Accepted && result.Receipt is { } receipt
                ? RentalPackets.ChargeSuccess(request.Type, receipt.Character.Inventory.AccountWallet.RentalPoints)
                : RentalPackets.ChargeFailure(), result.Accepted ? "rental_charge_success" : "rental_charge_rejected");
        await PublishStateAsync(context, result, "rental_state_after_charge");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> RentAsync(GameConnectionContext context, Packet packet)
    {
        RentalEquipmentRequest request;
        try { request = RentalRequests.Equipment(packet.Body); }
        catch (InvalidDataException ex)
        {
            log.Write("rental_equipment_rejected", new { Connection = context.Id, Reason = ex.Message });
            await context.Reply(CmdPacketType.RENT_EQUIPMENT_ITEM, RentalPackets.RentFailure(), "rental_equipment_malformed");
            return CommandResult.Handled;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        InventoryResult result = context.BlocksTownActions ? InventoryResult.Reject() :
            context.Session.RentEquipment(rental, checked((int)request.ItemId), request.Job, request.Tier, now);
        await context.Reply(CmdPacketType.RENT_EQUIPMENT_ITEM,
            result.Accepted ? RentalPackets.RentSuccess() : RentalPackets.RentFailure(result.Error == 17 ? (byte)2 : (byte)1),
            result.Accepted ? "rental_equipment_success" : "rental_equipment_rejected",
            result.Receipt is { } receipt ? () => context.Session.CanPublishInventory(receipt) : null);
        await PublishStateAsync(context, result, "rental_state_after_equipment");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    static async Task PublishStateAsync(GameConnectionContext context, InventoryResult result, string action)
    {
        if (!result.Accepted || result.Receipt is not { } receipt)
            return;
        await context.Notify(NotiPacketType.EQUIPMENT_RENTAL_LIST, RentalPackets.State(receipt.Character.Inventory, DateTimeOffset.UtcNow), action,
            () => context.Session.CanPublishInventory(receipt));
    }
}
