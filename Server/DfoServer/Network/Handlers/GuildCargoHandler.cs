using DfoServer.Game.Guild;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class GuildCargoHandler(GuildCargoService service, GuildCargoProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.GUILD_CARGO, QueryAsync, nameof(GuildCargoHandler));
        commands.Register(CmdPacketType.GUILD_CARGO_HISTORY, QueryAsync, nameof(GuildCargoHandler));
        commands.Register(CmdPacketType.GUILD_CARGO_PUSH_ITEM, DepositAsync, nameof(GuildCargoHandler));
        commands.Register(CmdPacketType.GUILD_CARGO_POP_ITEM, WithdrawAsync, nameof(GuildCargoHandler));
        commands.Register(CmdPacketType.GUILD_CARGO_MOVE_ITEM, MoveAsync, nameof(GuildCargoHandler));
    }

    async Task<CommandResult> QueryAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 0 || context.BlocksTownActions) return await RejectAsync(context, request, 8);
        await projection.QueryAsync(context, request.Opcode == (ushort)CmdPacketType.GUILD_CARGO_HISTORY);
        return CommandResult.Handled;
    }

    Task<CommandResult> DepositAsync(GameConnectionContext context, Packet request) =>
        GuildCargoRequests.TryDeposit(request.Body, out var transfer) ? TransferAsync(context, request, transfer) : RejectAsync(context, request, 198);

    Task<CommandResult> WithdrawAsync(GameConnectionContext context, Packet request) =>
        GuildCargoRequests.TryWithdraw(request.Body, out var transfer) ? TransferAsync(context, request, transfer) : RejectAsync(context, request, 198);

    Task<CommandResult> MoveAsync(GameConnectionContext context, Packet request) =>
        GuildCargoRequests.TryMove(request.Body, out var transfer) ? TransferAsync(context, request, transfer) : RejectAsync(context, request, 8);

    async Task<CommandResult> RejectAsync(GameConnectionContext context, Packet request, ushort error)
    {
        await context.Reply((CmdPacketType)request.Opcode, GuildCargoPackets.Failure(error), "guild_cargo_rejected");
        return CommandResult.Handled;
    }

    async Task<CommandResult> TransferAsync(GameConnectionContext context, Packet request, GuildCargoRequest transfer)
    {
        if (context.BlocksTownActions) return await RejectAsync(context, request, 8);
        var result = context.Session.TransferGuildCargo(service, transfer, context.Session.GuildRequestIdentity(request.RequestSerial));
        await projection.TransferAsync(context, transfer, result);
        service.Publish(result.Commit, context.Session);
        log.Write("guild_cargo_transfer", new { Connection = context.Id, Character = result.Commit.Character?.Id,
            Guild = result.Commit.View?.GuildId, Deposit = transfer.Deposit, transfer.Move, transfer.ItemId, transfer.Count,
            Error = result.Commit.Error, result.Commit.Replayed, Revision = result.Commit.View?.Revision });
        return CommandResult.Handled;
    }
}
