using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class TextureChangeHandler(InventoryTextureChangeService textures,
    InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.EQUIPMENT_TEXTURE_CHANGE_REQUEST, Handle, nameof(TextureChangeHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        EquipmentTextureChangeRequest request;
        try { request = EquipmentTextureChangeRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.EQUIPMENT_TEXTURE_CHANGE_REQUEST,
                CargoPackets.Ack(InventoryResult.Reject(19)), "equipment_texture_change_malformed");
            return CommandResult.Handled;
        }

        var result = context.BlocksTownActions ? InventoryResult.Reject(19) :
            context.Session.PlanAndCommitInventory(character => textures.Apply(character, request), 19);
        var receipt = result.Receipt;
        log.Write("equipment_texture_change", new
        {
            Connection = context.Id,
            request.Slot,
            request.Material,
            result.Accepted,
            result.Error,
            Revision = receipt?.Character.Revision,
            ItemId = receipt?.Index.Find(new(InventoryListType.Main, request.Slot))?.Core.ItemId,
            Gold = receipt?.Character.Inventory.Gold
        });
        await projection.RefreshAsync(context, result);
        await context.Reply(CmdPacketType.EQUIPMENT_TEXTURE_CHANGE_REQUEST, CargoPackets.Ack(result),
            result.Accepted ? "equipment_texture_change_committed" : "equipment_texture_change_rejected",
            receipt is not null ? () => context.Session.CanPublishInventory(receipt) : null);
        return CommandResult.Handled;
    }
}
