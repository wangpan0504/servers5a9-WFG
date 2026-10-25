using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class GuildCompoundHandler(InventoryGuildCompoundService compounds,
    InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.COMPOUND_GEM, HandleGem, nameof(GuildCompoundHandler));
        commands.Register(CmdPacketType.COMPOUND_FLAG, HandleMedal, nameof(GuildCompoundHandler));
    }

    Task<CommandResult> HandleGem(GameConnectionContext context, Packet packet) =>
        Handle(context, packet, CmdPacketType.COMPOUND_GEM, GuildCompoundKind.GuardianGem);

    Task<CommandResult> HandleMedal(GameConnectionContext context, Packet packet) =>
        Handle(context, packet, CmdPacketType.COMPOUND_FLAG, GuildCompoundKind.GuildMedal);

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet,
        CmdPacketType opcode, GuildCompoundKind kind)
    {
        GuildCompoundRequest request;
        try { request = GuildCompoundRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("guild_compound", new
            {
                Connection = context.Id,
                Opcode = (ushort)opcode,
                Length = packet.Body.Length,
                Accepted = false,
                Reason = "malformed"
            });
            await context.Reply(opcode, GuildCompoundPackets.Failure(1), "guild_compound_malformed");
            await projection.RefreshListAsync(context, InventoryListType.GuildMedal, "guild_compound_refresh");
            return CommandResult.Handled;
        }

        var original = context.Owner.Execute(() =>
            (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        GuildCompoundReceipt? receipt = null;
        string? rejection = null;
        var result = context.BlocksTownActions
            ? InventoryResult.Reject(1)
            : context.Session.CompoundGuild(kind, request, compounds, out receipt, out rejection);
        rejection ??= result.Accepted ? null : "blocked-state";
        bool CanSend() => result.Receipt is { } committed
            ? context.Session.CanPublishInventory(committed)
            : context.Owner.Execute(() => context.Session.SelectionEpoch == original.SelectionEpoch &&
                context.Session.Phase == original.Phase && ReferenceEquals(context.Session.Selected, original.Selected));

        log.Write("guild_compound", new
        {
            Connection = context.Id,
            Kind = kind.ToString(),
            result.Accepted,
            result.Error,
            Reason = result.Accepted
                ? receipt?.Success == true ? "upgraded" : "same-grade"
                : rejection,
            Grade = request.Grade,
            Materials = request.Count,
            receipt?.NextGrade,
            receipt?.SuccessRate,
            receipt?.Success,
            Product = receipt?.ProductItemId,
            Revision = result.Receipt?.Character.Revision
        });
        if (!result.Accepted)
        {
            // 客户端提交前已把所选材料移出 list38；拒绝没有提交计划，必须显式整列重发。
            await context.Reply(opcode, GuildCompoundPackets.Failure(result.Error == 95 ? (ushort)95 : (ushort)1),
                "guild_compound_rejected", CanSend);
            await projection.RefreshListAsync(context, InventoryListType.GuildMedal, "guild_compound_refresh");
            return CommandResult.Handled;
        }
        if (receipt is null)
            throw new InvalidOperationException("公会合成成功缺少业务回执");

        // Accepted plans always contain a product; rejection never commits consumption.
        await context.Reply(opcode, GuildCompoundPackets.Success(receipt.ProductItemId),
            "guild_compound_committed", CanSend);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
