using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class EquipmentEffectHandler(InventoryEquipmentEffectService service, InventoryProjection projection,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.ADD_EQUIPMENT_EFFECT, Handle, nameof(EquipmentEffectHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        const CmdPacketType opcode = CmdPacketType.ADD_EQUIPMENT_EFFECT;
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool SameSelection() => context.Owner.Execute(() => ReferenceEquals(captured.Selected, context.Session.Selected) &&
            captured.SelectionEpoch == context.Session.SelectionEpoch && captured.Phase == context.Session.Phase);
        EquipmentEffectRequest request;
        try { request = EquipmentEffectRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("equipment_effect", new { Connection = context.Id, Accepted = false, Reason = "malformed", Length = packet.Body.Length });
            await context.Reply(opcode, CargoPackets.Ack(InventoryResult.Reject(19)), "equipment_effect_malformed", SameSelection);
            return CommandResult.Handled;
        }
        var result = context.BlocksTownActions ? InventoryResult.Reject(19) : context.Session.PlanAndCommitInventory(
            character => service.Plan(character, request.SourceSlot, request.TargetSlot), 19,
            usedItem: new(InventoryListType.Main, request.SourceSlot), useTarget: new(InventoryListType.Main, request.TargetSlot));
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : SameSelection();
        log.Write("equipment_effect", new { Connection = context.Id, request.SourceSlot, request.TargetSlot,
            result.Accepted, result.Error, Revision = result.Receipt?.Character.Revision });
        // 成功窗读取目标实际实例，先安装权威差量再展示结果。
        await projection.RefreshAsync(context, result);
        await context.Reply(opcode, EquipmentEffectPackets.Ack(result, request.TargetSlot),
            result.Accepted ? "equipment_effect_committed" : "equipment_effect_rejected", Guard);
        return CommandResult.Handled;
    }
}
