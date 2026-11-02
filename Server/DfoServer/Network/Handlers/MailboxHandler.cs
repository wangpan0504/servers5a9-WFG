using DfoServer.Game.Mailbox;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class MailboxHandler(MailboxService service, MailboxProjection projection, InventoryProjection inventory) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.MAILBOX_SEND, Send, nameof(MailboxHandler));
        commands.Register(CmdPacketType.MULTI_MAILBOX_SEND, Send, nameof(MailboxHandler));
        commands.Register(CmdPacketType.MAILBOX_OPEN, Open, nameof(MailboxHandler));
        commands.Register(CmdPacketType.MAILBOX_EXTRACT_ITEM, Extract, nameof(MailboxHandler));
        commands.Register(CmdPacketType.CHANGE_LETTER_STAT, State, nameof(MailboxHandler));
        commands.Register(CmdPacketType.QUERY_CHARAC_INFO_MAILBOX, Query, nameof(MailboxHandler));
    }

    async Task<CommandResult> Open(GameConnectionContext context, Packet packet)
    {
        MailboxRequests.Open(packet.Body);
        if (context.BlocksTownActions)
        {
            await context.Reply(CmdPacketType.MAILBOX_OPEN, MailboxPackets.Result(MailboxResult.Reject(MailboxError.InvalidState)), "mailbox_open_rejected");
            return CommandResult.Handled;
        }
        var page = context.Session.OpenMailbox(service);
        await context.Reply(CmdPacketType.MAILBOX_OPEN, MailboxPackets.Open(page.Page), "mailbox_open", () => context.Session.CanPublishMailbox(page.Receipt));
        await projection.ListAsync(context, page.Page, page.Receipt);
        return CommandResult.Handled;
    }
    async Task<CommandResult> Send(GameConnectionContext context, Packet packet)
    {
        var opcode = (CmdPacketType)packet.Opcode;
        var request = MailboxRequests.Send(packet.Body, opcode == CmdPacketType.MULTI_MAILBOX_SEND);
        if (request.Special != 0 || request.RecipientServerIndex != 0 && request.RecipientServerIndex != context.Channel.ServerIndex)
            return CommandResult.Unhandled;
        // 查询和寄送共用目标服务器选择值；确认本服后才交给仅支持本服的领域寄送合同。
        if (request.RecipientServerIndex != 0)
            request = request with { RecipientServerIndex = 0 };
        var result = context.BlocksTownActions ? MailboxResult.Reject(MailboxError.InvalidState) : context.Session.SendMail(service, request);
        projection.Arrived(result);
        await context.Reply(opcode, MailboxPackets.Result(result), "mail_send_result", () => MailboxProjection.CanPublish(context, result));
        if (result.Inventory is { } change)
            await inventory.RefreshAsync(context, change);
        else if (!result.Accepted)
            await MailboxProjection.RestoreSendAsync(context, request, result);
        return CommandResult.Handled;
    }
    async Task<CommandResult> Extract(GameConnectionContext context, Packet packet)
    {
        var ids = MailboxRequests.Extract(packet.Body);
        var result = context.BlocksTownActions ? MailboxResult.Reject(MailboxError.InvalidState) : context.Session.ClaimMail(service, ids);
        await context.Reply(CmdPacketType.MAILBOX_EXTRACT_ITEM, result.Accepted ? MailboxPackets.Extract(result.Claimed) : MailboxPackets.Result(result, extracting: true),
            "mail_claim_result", () => MailboxProjection.CanPublish(context, result));
        if (result.Inventory is { } change)
        {
            await inventory.RefreshAsync(context, change);
            if (change.Plan is { } plan && MailboxProjection.CanPublish(context, result) &&
                DfoServer.Game.BattlePass.BattlePassUndefeatedSongMailSource.AcquiredAmount(plan) > 0)
                await BattlePassMissionProjection.RefreshAsync(context, DateTimeOffset.UtcNow);
        }
        // ACK95由原生消费者更新当前邮件的附件及逐项错误；清空式97会使打开的窗口失去原对象。
        return CommandResult.Handled;
    }
    async Task<CommandResult> State(GameConnectionContext context, Packet packet)
    {
        var request = MailboxRequests.State(packet.Body);
        var result = context.BlocksTownActions ? MailboxResult.Reject(MailboxError.InvalidState) : context.Session.ChangeMailState(service, request.Ids, request.State);
        bool CanSend() => MailboxProjection.CanPublish(context, result);
        await context.Reply(CmdPacketType.CHANGE_LETTER_STAT, result.Accepted ? MailboxPackets.State(result.Changes) : MailboxPackets.Result(result, state: true), "mail_state_result", CanSend);
        if (result.Accepted && request.State == 0)
        {
            await context.Notify(NotiPacketType.MAILBOX_REMOVE_MAIL, MailboxPackets.Remove(request.Ids), "mail_removed", CanSend);
            if (result.Receipt is { } receipt && context.Session.RefreshMailbox(service, receipt) is { } page)
                await projection.ListAsync(context, page.Page, page.Receipt);
        }
        return CommandResult.Handled;
    }
    async Task<CommandResult> Query(GameConnectionContext context, Packet packet)
    {
        var request = MailboxRequests.Query(packet.Body);
        if (request.RecipientServerIndex != 0 && request.RecipientServerIndex != context.Channel.ServerIndex)
        {
            await context.Reply(CmdPacketType.QUERY_CHARAC_INFO_MAILBOX, MailboxPackets.QueryServerUnavailable(), "mail_recipient_server_unavailable");
            return CommandResult.Handled;
        }
        var result = context.BlocksTownActions ? MailboxResult.Reject(MailboxError.InvalidState) : context.Session.QueryMailRecipient(service, request.Name);
        await context.Reply(CmdPacketType.QUERY_CHARAC_INFO_MAILBOX, MailboxPackets.Query(result, context.Channel.ServerIndex), "mail_recipient", () => MailboxProjection.CanPublish(context, result));
        return CommandResult.Handled;
    }
}
