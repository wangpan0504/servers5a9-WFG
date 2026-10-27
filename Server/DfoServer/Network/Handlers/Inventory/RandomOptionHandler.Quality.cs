using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed partial class RandomOptionHandler
{
    async Task<CommandResult> ResetQuality(GameConnectionContext context, Packet packet)
    {
        bool randomOptions = packet.Opcode == (ushort)CmdPacketType.RESET_RANDOM_OPTION;
        var opcode = randomOptions ? CmdPacketType.RESET_RANDOM_OPTION : CmdPacketType.RESET_ITEM_ATTR;
        EquipmentQualityRequest request;
        try { request = EquipmentQualityRequests.Read(packet.Body, randomOptions); }
        catch (InvalidDataException)
        {
            await context.Reply(opcode, CargoPackets.Ack(InventoryResult.Reject(19)), "equipment_quality_malformed");
            return CommandResult.Handled;
        }
        var result = context.Activity?.BlocksTownActions == true || !randomOptions && request is { MaterialSlot: 0, ClientState: 1 } &&
            !context.Session.HasEquipmentSoulNpc(true) ? InventoryResult.Reject(19) :
            context.Session.PlanAndCommitInventory(character => reseal?.Handles(character, request) == true
                ? reseal.Plan(character, request) : options.ResetQuality(character, request), 19);
        var receipt = result.Receipt;
        log.Write("equipment_quality_reset", new { Connection = context.Id, request.TargetSlot, request.MaterialSlot,
            request.ClientState, RandomOptions = randomOptions, result.Accepted, result.Error,
            Revision = receipt?.Character.Revision, Quality = receipt?.Index.Find(new(InventoryListType.Main, request.TargetSlot))?.Core.Value });
        byte[] ack = CargoPackets.Ack(result);
        if (result.Accepted && !randomOptions)
        {
            ack = new byte[9]; ack[0] = 1;
            BinaryPrimitives.WriteInt16LittleEndian(ack.AsSpan(1), request.MaterialSlot);
            BinaryPrimitives.WriteInt32LittleEndian(ack.AsSpan(3), receipt!.Index.Find(new(InventoryListType.Main, request.MaterialSlot))?.Core.Count ?? 0);
            BinaryPrimitives.WriteUInt16LittleEndian(ack.AsSpan(7), result.Plan!.Reason.StartsWith("equipment_reseal", StringComparison.Ordinal) ? (ushort)1 : (ushort)2);
        }
        await projection.RefreshAsync(context, result);
        await context.Reply(opcode, ack, result.Accepted ? "equipment_quality_committed" : "equipment_quality_rejected",
            receipt is not null ? () => context.Session.CanPublishInventory(receipt) : null);
        return CommandResult.Handled;
    }
}
