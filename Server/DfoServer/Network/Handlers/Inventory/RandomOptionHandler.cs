using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class RandomOptionHandler(InventoryRandomOptionService options, InventoryProjection projection,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.UNSEAL_RANDOM_OPTION, Handle, nameof(RandomOptionHandler));
        commands.Register(CmdPacketType.CHANGE_RANDOM_OPTION, Change, nameof(RandomOptionHandler));
    }

    async Task<CommandResult> Change(GameConnectionContext context, Packet packet)
    {
        EquipmentRandomOptionChangeRequest request;
        try { request = RandomOptionRequests.Change(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("random_option_change", new { Connection = context.Id, Length = packet.Body.Length, Accepted = false, Result = "malformed" });
            await context.Reply(CmdPacketType.CHANGE_RANDOM_OPTION, CargoPackets.Ack(InventoryResult.Reject(19)), "random_option_change_malformed");
            return CommandResult.Handled;
        }
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
            context.Session.PlanAndCommitInventory(character => options.Change(character, request), 19, requireTown: false);
        log.Write("random_option_change", new { Connection = context.Id, Slot = request.Slot, request.OptionIndex,
            result.Accepted, result.Error, Result = result.Accepted ? "committed" : "rejected", Revision = result.Receipt?.Character.Revision });
        await projection.RefreshAsync(context, result);
        await context.Reply(CmdPacketType.CHANGE_RANDOM_OPTION, CargoPackets.Ack(result),
            result.Accepted ? "random_option_changed" : "random_option_change_rejected",
            result.Receipt is { } receipt ? () => context.Session.CanPublishInventory(receipt) : null);
        return CommandResult.Handled;
    }

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        UnsealRandomOptionRequest request;
        try { request = RandomOptionRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.UNSEAL_RANDOM_OPTION, RandomOptionPackets.Status(false), "random_option_malformed");
            return CommandResult.Handled;
        }
        var result = context.Session.PlanAndCommitInventory(character => options.Unseal(character, request), requireTown: false);
        log.Write(result.Accepted ? "random_option_unseal" : "random_option_rejected", new { Connection = context.Id, Slot = request.Slot,
            result.Accepted, Result = result.Accepted ? "committed" : "rejected", Revision = result.Receipt?.Character.Revision });
        // 原生窗口先读取已提交的词条和金币，再结束解封等待；不重建全量背包。
        await projection.RefreshAsync(context, result);
        await context.Reply(CmdPacketType.UNSEAL_RANDOM_OPTION, RandomOptionPackets.Status(result.Accepted),
            result.Accepted ? "random_option_unsealed" : "random_option_rejected",
            result.Receipt is { } receipt ? () => context.Session.CanPublishInventory(receipt) : null);
        return CommandResult.Handled;
    }
}
