using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class RepairHandler(InventoryRepairService repair, InventoryProjection projection, Infrastructure.ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.REPAIR_EQUIPMENT, Handle, nameof(RepairHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var request = RepairRequests.Read(packet.Body);
        var original = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
            context.Session.RepairEquipment(request, repair);
        if (request.Mode == 1)
            log.Write("devil_auto_repair", new { Connection = context.Id, result.Accepted, result.Error,
                Revision = result.Receipt?.Character.Revision });
        bool CanSend() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            context.Session.SelectionEpoch == original.SelectionEpoch && context.Session.Phase == original.Phase &&
            ReferenceEquals(context.Session.Selected, original.Selected));
        await context.Reply(CmdPacketType.REPAIR_EQUIPMENT, RepairPackets.Ack(request, result),
            result.Accepted ? "equipment_repaired" : "equipment_repair_rejected", CanSend);
        HashSet<InventorySlot>? consumed = null;
        if (result.Accepted && request.RepairItemSlot >= 0 && result.Receipt is { } used)
        {
            // ACK23只修理目标，不扣源券；13完整载入同步真实剩余量，避免把消费当作获得通知。
            consumed = [new(InventoryListType.Main, request.RepairItemSlot)];
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.Full(used.Character.Inventory), "repair_item_remaining", CanSend);
        }
        try
        {
            await projection.RefreshAsync(context, result, consumed);
            if (result.Accepted && result.Plan?.EquipmentRepair is { } fact && CanSend())
                await DfoServer.Network.Projection.BattlePassMissionProjection.RefreshAsync(context, fact.Clock.GetUtcNow());
        }
        catch (System.Data.Common.DbException error) when (result.Accepted && result.Plan?.EquipmentRepair is not null)
        {
            // 修理与任务已提交，后置读取失败不能重放扣费或道具消耗。
            throw new IOException("Committed equipment repair publication failed", error);
        }
        return CommandResult.Handled;
    }
}
