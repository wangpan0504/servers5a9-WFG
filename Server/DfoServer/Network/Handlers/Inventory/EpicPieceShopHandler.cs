using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class EpicPieceShopHandler(EpicPieceShopService service, InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.EPIC_PIECE_EXCHANGE_CHOICE, Select, nameof(EpicPieceShopHandler));
        commands.Register(CmdPacketType.EPIC_PIECE_EXCHANGE_MATERIAL_CHOICE, Refresh, nameof(EpicPieceShopHandler));
        commands.Register(CmdPacketType.EPIC_PIECE_EXCHANGE_COMPLETE, Complete, nameof(EpicPieceShopHandler));
    }
    Task<CommandResult> Select(GameConnectionContext context, Packet packet) => Handle(context,
        CmdPacketType.EPIC_PIECE_EXCHANGE_CHOICE, EpicPieceShopAction.Select, EpicPieceShopRequests.Select(packet.Body));
    Task<CommandResult> Refresh(GameConnectionContext context, Packet packet)
    {
        EpicPieceShopRequests.Refresh(packet.Body);
        return Handle(context, CmdPacketType.EPIC_PIECE_EXCHANGE_MATERIAL_CHOICE, EpicPieceShopAction.Refresh, 0);
    }
    Task<CommandResult> Complete(GameConnectionContext context, Packet packet) => Handle(context,
        CmdPacketType.EPIC_PIECE_EXCHANGE_COMPLETE, EpicPieceShopAction.Complete, EpicPieceShopRequests.Complete(packet.Body));

    async Task<CommandResult> Handle(GameConnectionContext context, CmdPacketType opcode, EpicPieceShopAction action, int argument)
    {
        var operation = context.Session.OperateEpicPieceShop(service, action, argument);
        var result = operation.Result;
        bool Guard() => operation.Scene is { } scene && context.Session.CanPublishFormalDungeon(scene);
        log.Write("epic_piece_shop", new { Connection = context.Id, Action = action.ToString(), Argument = argument,
            result.Accepted, result.Error, Reason = result.RejectReason, Revision = result.Receipt?.Character.Revision });
        await context.Reply(opcode, EpicPieceShopPackets.Result(result), "epic_piece_shop_ack", operation.Scene is null ? null : Guard);
        if (!result.Accepted)
        {
            if (action == EpicPieceShopAction.Complete && result.RejectReason == "exchange_material_required" && operation.Scene is not null)
                await context.Notify(NotiPacketType.SERVER_NOTICE_MESSAGE,
                    ServerNoticePackets.Information(argument == 0 ? "交换所需的史诗碎片不足。" : "史诗灵魂不足。"),
                    "epic_piece_shop_material_required", Guard);
            return CommandResult.Handled;
        }
        await projection.RefreshAsync(context, result);
        if (action != EpicPieceShopAction.Complete)
            await context.Notify(NotiPacketType.EPIC_PIECE_EXCHANGE_METERIAL_LIST,
                EpicPieceShopPackets.Materials(operation.Materials, service.Rules.Received), "epic_piece_shop_materials", Guard);
        return CommandResult.Handled;
    }
}
