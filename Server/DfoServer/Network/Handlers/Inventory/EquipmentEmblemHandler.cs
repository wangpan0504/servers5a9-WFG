using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class EquipmentEmblemHandler(InventoryEquipmentEmblemService service,
    InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.USE_EMBLEM_FOR_EQUIPMENT, Use, nameof(EquipmentEmblemHandler));
        commands.Register(CmdPacketType.ADD_EQUIPMENT_SOCKET, Open, nameof(EquipmentEmblemHandler));
    }

    async Task<CommandResult> Use(GameConnectionContext context, Packet packet)
    {
        EquipmentEmblemRequest request;
        try { request = EquipmentEmblemRequests.Use(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("equipment_emblem", new
            {
                Connection = context.Id,
                Length = packet.Body.Length,
                Accepted = false,
                Reason = "malformed"
            });
            await context.Reply(CmdPacketType.USE_EMBLEM_FOR_EQUIPMENT, EquipmentEmblemPackets.Failure(),
                "equipment_emblem_malformed");
            return CommandResult.Handled;
        }

        var capture = context.Owner.Execute(() =>
            (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        EquipmentEmblemReceipt? receipt = null;
        string? rejection = null;
        var result = context.BlocksTownActions
            ? InventoryResult.Reject(InventoryEquipmentEmblemService.Rejection)
            : context.Session.PlanAndCommitInventory(character =>
                service.UseEmblems(character, request, out receipt, out rejection),
                InventoryEquipmentEmblemService.Rejection);
        bool Guard() => result.Receipt is { } committed
            ? context.Session.CanPublishInventory(committed)
            : context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
                capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        log.Write("equipment_emblem", new
        {
            Connection = context.Id,
            request.TargetSlot,
            request.TargetItemId,
            Count = request.Emblems.Length,
            result.Accepted,
            result.Error,
            Reason = result.Accepted ? "committed" : rejection ?? "state-or-snapshot",
            Revision = result.Receipt?.Character.Revision
        });
        if (!result.Accepted)
        {
            await context.Reply(CmdPacketType.USE_EMBLEM_FOR_EQUIPMENT, EquipmentEmblemPackets.Failure(result.Error),
                "equipment_emblem_rejected", Guard);
            return CommandResult.Handled;
        }

        var committed = result.Receipt ?? throw new InvalidOperationException("装备徽章成功缺少提交回执");
        var applied = receipt ?? throw new InvalidOperationException("装备徽章成功缺少业务回执");
        if (committed.Index.Find(applied.Target) is null)
            throw new InvalidOperationException("装备徽章提交后缺少目标实例");
        var updates = applied.Materials.Select(material => new EquipmentEmblemRemaining(material.Slot,
            committed.Index.Find(new(InventoryListType.Main, material.Slot))?.Core.Count ?? 0)).ToArray();
        var acknowledged = applied.Materials.Select(material =>
            new InventorySlot(InventoryListType.Main, material.Slot)).ToHashSet();
        // ACK915以绝对数量更新材料；目标本身不在ACK中，须先发布已提交目标差量。
        await projection.RefreshAsync(context, result, acknowledgedSlots: acknowledged);
        await context.Reply(CmdPacketType.USE_EMBLEM_FOR_EQUIPMENT, EquipmentEmblemPackets.Used(updates),
            "equipment_emblem_committed", () => context.Session.CanPublishInventory(committed));
        return CommandResult.Handled;
    }

    async Task<CommandResult> Open(GameConnectionContext context, Packet packet)
    {
        EquipmentSocketRequest request;
        try { request = EquipmentEmblemRequests.Open(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("equipment_socket", new
            {
                Connection = context.Id,
                Length = packet.Body.Length,
                Accepted = false,
                Reason = "malformed"
            });
            await context.Reply(CmdPacketType.ADD_EQUIPMENT_SOCKET, EquipmentEmblemPackets.Failure(),
                "equipment_socket_malformed");
            return CommandResult.Handled;
        }

        var capture = context.Owner.Execute(() =>
            (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        EquipmentSocketReceipt? receipt = null;
        string? rejection = null;
        var result = context.BlocksTownActions
            ? InventoryResult.Reject(InventoryEquipmentEmblemService.Rejection)
            : context.Session.PlanAndCommitInventory(character =>
                service.OpenSockets(character, request, out receipt, out rejection),
                InventoryEquipmentEmblemService.Rejection);
        bool Guard() => result.Receipt is { } committed
            ? context.Session.CanPublishInventory(committed)
            : context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
                capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        log.Write("equipment_socket", new
        {
            Connection = context.Id,
            request.TargetSlot,
            request.TargetItemId,
            request.MaterialSlot,
            result.Accepted,
            result.Error,
            Reason = result.Accepted ? "committed" : rejection ?? "state-or-snapshot",
            Revision = result.Receipt?.Character.Revision
        });
        if (!result.Accepted)
        {
            await context.Reply(CmdPacketType.ADD_EQUIPMENT_SOCKET, EquipmentEmblemPackets.Failure(result.Error),
                "equipment_socket_rejected", Guard);
            return CommandResult.Handled;
        }

        var committed = result.Receipt ?? throw new InvalidOperationException("装备开孔成功缺少提交回执");
        var opened = receipt ?? throw new InvalidOperationException("装备开孔成功缺少业务回执");
        if (committed.Index.Find(opened.Target) is null)
            throw new InvalidOperationException("装备开孔提交后缺少目标实例");
        var acknowledged = new HashSet<InventorySlot>
        {
            new(InventoryListType.Main, opened.MaterialSlot)
        };
        // ACK916负责材料数量变化，目标完整核心先由普通库存投影更新。
        await projection.RefreshAsync(context, result, acknowledgedSlots: acknowledged);
        await context.Reply(CmdPacketType.ADD_EQUIPMENT_SOCKET,
            EquipmentEmblemPackets.SocketOpened(opened.Target.Index, opened.MaterialSlot),
            "equipment_socket_committed", () => context.Session.CanPublishInventory(committed));
        return CommandResult.Handled;
    }
}
