using DfoServer.Game.CollectBox;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class CollectBoxHandler(CollectBoxService service, CollectBoxProjection boxes,
    InventoryProjection inventory, TimeProvider? clock = null) : IGameCommandModule
{
    readonly TimeProvider timer = clock ?? TimeProvider.System;

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SELECT_COLLECTBOX, Select, nameof(CollectBoxHandler));
        commands.Register(CmdPacketType.ADD_COLLECTBOX_ITEM, Add, nameof(CollectBoxHandler));
        commands.Register(CmdPacketType.REMOVE_COLLECTBOX_ITEM, Remove, nameof(CollectBoxHandler));
        commands.Register(CmdPacketType.EXTEND_COLLECTBOX_EXPIRY_DATE, Extend, nameof(CollectBoxHandler));
    }

    async Task<CommandResult> Select(GameConnectionContext context, Packet packet)
    {
        byte box;
        CollectBoxResult result;
        try
        {
            box = CollectBoxRequests.Select(packet.Body);
            result = context.Session.PlanAndCommitCollectBox(character => service.Select(character, box),
                CollectBoxService.ErrorUnavailable);
        }
        catch (InvalidDataException)
        {
            box = 0;
            result = CollectBoxResult.Reject(CollectBoxService.ErrorUnavailable);
        }
        await context.Reply(CmdPacketType.SELECT_COLLECTBOX, CollectBoxPackets.Select(box, result), "collectbox_select",
            result.Receipt is { } receipt ? () => context.Session.CanPublishCollectBox(receipt) : null);
        return CommandResult.Handled;
    }

    async Task<CommandResult> Add(GameConnectionContext context, Packet packet)
    {
        CollectBoxAddRequest request;
        CollectBoxResult result;
        try
        {
            request = CollectBoxRequests.Add(packet.Body);
            result = context.Session.PlanAndCommitCollectBox(
                character => service.Add(character, request, timer.GetUtcNow()), CollectBoxService.ErrorUnavailable);
        }
        catch (InvalidDataException)
        {
            request = default;
            result = CollectBoxResult.Reject(CollectBoxService.ErrorInvalidItem);
        }
        await PublishMutation(context, CmdPacketType.ADD_COLLECTBOX_ITEM, request.Box, result,
            CollectBoxPackets.Mutation(result), "collectbox_add");
        return CommandResult.Handled;
    }

    async Task<CommandResult> Remove(GameConnectionContext context, Packet packet)
    {
        CollectBoxRemoveRequest request;
        CollectBoxResult result;
        try
        {
            request = CollectBoxRequests.Remove(packet.Body);
            result = context.Session.PlanAndCommitCollectBox(
                character => service.Remove(character, request, timer.GetUtcNow()), CollectBoxService.ErrorMissing);
        }
        catch (InvalidDataException)
        {
            request = default;
            result = CollectBoxResult.Reject(CollectBoxService.ErrorMissing);
        }
        await PublishMutation(context, CmdPacketType.REMOVE_COLLECTBOX_ITEM, request.Box, result,
            CollectBoxPackets.Mutation(result), "collectbox_remove");
        return CommandResult.Handled;
    }

    async Task<CommandResult> Extend(GameConnectionContext context, Packet packet)
    {
        CollectBoxExtendRequest request;
        CollectBoxResult result;
        try
        {
            request = CollectBoxRequests.Extend(packet.Body);
            result = context.Session.PlanAndCommitCollectBox(
                character => service.Extend(character, request, timer.GetUtcNow()), CollectBoxService.ErrorUnavailable);
        }
        catch (InvalidDataException)
        {
            request = default;
            result = CollectBoxResult.Reject(CollectBoxService.ErrorInvalidItem);
        }
        await PublishMutation(context, CmdPacketType.EXTEND_COLLECTBOX_EXPIRY_DATE, request.Box, result,
            CollectBoxPackets.Extension(request, result), "collectbox_extend");
        return CommandResult.Handled;
    }

    async Task PublishMutation(GameConnectionContext context, CmdPacketType opcode, byte box, CollectBoxResult result,
        byte[] acknowledgement, string action)
    {
        if (result.Accepted && result.Receipt is { } receipt)
        {
            await inventory.RefreshAsync(context, result.Inventory);
            await boxes.RefreshAsync(context, receipt, box);
        }
        await context.Reply(opcode, acknowledgement, action,
            result.Receipt is { } committed ? () => context.Session.CanPublishCollectBox(committed) : null);
    }
}
