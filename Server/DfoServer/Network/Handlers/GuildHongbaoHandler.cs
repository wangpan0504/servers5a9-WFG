using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class GuildHongbaoHandler(GuildHongbaoService service, InventoryProjection inventory, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.GET_GUILD_HONGBAO_LIST, ListAsync, nameof(GuildHongbaoHandler));
        commands.Register(CmdPacketType.GET_GUILD_HONGBAO_HISTORY_LIST, HistoryAsync, nameof(GuildHongbaoHandler));
        commands.Register(CmdPacketType.GIVE_GUILD_HONGBAO, IssueAsync, nameof(GuildHongbaoHandler));
        commands.Register(CmdPacketType.TAKE_GUILD_HONGBAO, TakeAsync, nameof(GuildHongbaoHandler));
    }

    async Task<CommandResult> ListAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length == 0)
        {
            var refund = context.Session.UseGuildHongbao(service, GuildHongbaoAction.Refund);
            if (refund.Inventory is not null) await inventory.RefreshAsync(context, refund.Inventory, refreshEquipment: false);
        }
        if (context.Session.CaptureGuildPanelQuery() is not { } query) return CommandResult.Handled;
        var list = request.Body.Length == 0 ? service.Read(context.Owner, query.CharacterId) : null;
        await context.Reply(CmdPacketType.GET_GUILD_HONGBAO_LIST,
            list is null ? GuildHongbaoPackets.Rejected(true) : GuildHongbaoPackets.List(list),
            "guild_hongbao_list", query.IsCurrent);
        return CommandResult.Handled;
    }

    async Task<CommandResult> HistoryAsync(GameConnectionContext context, Packet request)
    {
        if (context.Session.CaptureGuildPanelQuery() is not { } query) return CommandResult.Handled;
        var history = GuildHongbaoRequests.TryRecord(request.Body, out uint id)
            ? service.History(context.Owner, query.CharacterId, id) : null;
        await context.Reply(CmdPacketType.GET_GUILD_HONGBAO_HISTORY_LIST,
            history is null ? GuildHongbaoPackets.Rejected(true) : GuildHongbaoPackets.History(history),
            "guild_hongbao_history", query.IsCurrent);
        return CommandResult.Handled;
    }

    Task<CommandResult> IssueAsync(GameConnectionContext context, Packet request) =>
        GuildHongbaoRequests.TryIssue(request.Body, out uint item, out byte count)
            ? MutateAsync(context, request, GuildHongbaoAction.Issue, item, count)
            : RejectAsync(context, request);

    Task<CommandResult> TakeAsync(GameConnectionContext context, Packet request) =>
        GuildHongbaoRequests.TryRecord(request.Body, out uint id)
            ? MutateAsync(context, request, GuildHongbaoAction.Take, id, 0)
            : RejectAsync(context, request);

    static async Task<CommandResult> RejectAsync(GameConnectionContext context, Packet request)
    {
        if (context.Session.CaptureGuildPanelQuery() is { } query)
            await context.Reply((CmdPacketType)request.Opcode, GuildHongbaoPackets.Rejected(false),
                "guild_hongbao_rejected", query.IsCurrent);
        return CommandResult.Handled;
    }

    async Task<CommandResult> MutateAsync(GameConnectionContext context, Packet request, GuildHongbaoAction action,
        uint value, byte count)
    {
        var use = context.Session.UseGuildHongbao(service, action, value, count);
        // 发行按本服规则只创建真实红包，不额外赠币；末字段用途未证，使用中性0。
        byte[] body = !use.Commit.Success ? GuildHongbaoPackets.Rejected(false) :
            GuildHongbaoPackets.Mutation(action == GuildHongbaoAction.Issue ? value : 0,
                action == GuildHongbaoAction.Take ? checked((uint)use.Commit.Amount) : 0);
        await context.Reply((CmdPacketType)request.Opcode, body, "guild_hongbao_mutation", use.IsCurrent);
        if (use.Inventory is not null) await inventory.RefreshAsync(context, use.Inventory, refreshEquipment: false);
        log.Write("guild_hongbao", new { Connection = context.Id, Action = action.ToString(), use.Commit.Id,
            use.Commit.Error, use.Commit.Amount, use.Commit.Taken });
        return CommandResult.Handled;
    }
}
