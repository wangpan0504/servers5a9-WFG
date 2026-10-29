using System.Collections.Immutable;
using DfoServer.Game.Pvp;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Projection;

namespace DfoServer.Network.Handlers;

internal sealed class PvpTotalTeamHandler(PvpTotalTeamService teams, ServerLog log, InventoryProjection inventory,
    PvpTotalTeamProjection projection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SET_PVP_TOTAL_MATCH_TEAM, SetAsync, nameof(PvpTotalTeamHandler));
        commands.Register(CmdPacketType.CHECK_PVP_TOTAL_MATCH_TEAM_NAME, CheckNameAsync, nameof(PvpTotalTeamHandler));
        commands.Register(CmdPacketType.SET_PVP_TOTAL_MATCH_TEAM_NAME, RenameAsync, nameof(PvpTotalTeamHandler));
    }

    async Task<CommandResult> RenameAsync(GameConnectionContext context, Packet packet)
    {
        var request = PvpMatchingRequests.Rename(packet.Body);
        var result = context.Session.RenamePvpTotalTeam(teams, request.Name, request.Slot, context.BlocksTownActions);
        log.Write("pvp_total_team_rename", new { Connection = context.Id, result.Failure,
            Team = result.Team?.Id, Revision = result.Team?.Revision, Consumed = result.Inventory?.Plan?.Changed == true });
        if (result.Actor is not { } actor)
        {
            // 028F82A0先读公共u16错误码，0233CAC0失败分支不再消费；0为本服中立拒绝值。
            await context.Reply(CmdPacketType.SET_PVP_TOTAL_MATCH_TEAM_NAME,
                PvpMatchingPackets.Rejected(0), "pvp_total_team_rename_rejected");
            return CommandResult.Handled;
        }
        bool Current() => context.Session.CanPublishPvpTotalTeamRename(actor);
        using var publication = context.Sender.QueuePublication(Current);
        await context.Reply(CmdPacketType.SET_PVP_TOTAL_MATCH_TEAM_NAME,
            PvpMatchingPackets.NameChanged(result.Team!.Name), "pvp_total_team_renamed", Current);
        if (result.Inventory is { } changed) await inventory.RefreshAsync(context, changed);
        return CommandResult.Handled;
    }

    async Task<CommandResult> SetAsync(GameConnectionContext context, Packet packet)
    {
        var request = PvpMatchingRequests.Team(packet.Body);
        // 编队允许普通等候房；具体阶段与房间门禁由Session校验，仍保留其它活动的占用。
        var result = teams.RegisterWithReceipt(context.Session, request.Name, request.CharacterIndices,
            context.Activity?.BlocksTownActions == true);
        log.Write("pvp_total_team_registration", new { Connection = context.Id, result.Failure, Team = result.Team?.Id,
            Revision = result.Team?.Revision });
        if (result.Selection is not { } selection)
            await context.Reply(CmdPacketType.SET_PVP_TOTAL_MATCH_TEAM,
                PvpMatchingPackets.Rejected(RegistrationError(result.Failure)), "pvp_total_team_rejected");
        else
        {
            bool Current() => context.Session.CanPublishPvpTotalRoster(selection);
            using var publication = context.Sender.QueuePublication(Current);
            await projection.PublishAsync(context, result.Team!, selection);
            await context.Reply(CmdPacketType.SET_PVP_TOTAL_MATCH_TEAM,
                PvpMatchingPackets.TeamAccepted(selection.Members.Select(row => row.RosterIndex).ToImmutableArray()),
                "pvp_total_team_registered", Current);
        }
        return CommandResult.Handled;
    }

    async Task<CommandResult> CheckNameAsync(GameConnectionContext context, Packet packet)
    {
        string name = PvpMatchingRequests.CheckName(packet.Body);
        var failure = teams.CheckName(context.Owner, name);
        // 0233C680仅消费公共成功字节；错误3提示重名，1/2是致命错误，159提示非法字符。
        byte[] body = failure == PvpTotalTeamFailure.None ? [1] :
            PvpMatchingPackets.Rejected(failure == PvpTotalTeamFailure.NameTaken ? (ushort)3 : (ushort)159);
        await context.Reply(CmdPacketType.CHECK_PVP_TOTAL_MATCH_TEAM_NAME, body, "pvp_total_team_name_checked");
        log.Write("pvp_total_team_name_check", new { Connection = context.Id, Failure = failure });
        return CommandResult.Handled;
    }

    // 0233C870与同版dstr.dat逐项核对，不能将阵容或状态拒绝都映射为通用159。
    static ushort RegistrationError(PvpTotalTeamFailure failure) => failure switch
    {
        PvpTotalTeamFailure.NameTaken => 1,
        PvpTotalTeamFailure.InvalidLineup => 2,
        PvpTotalTeamFailure.MissingName => 4,
        PvpTotalTeamFailure.ArenaActive => 2,
        PvpTotalTeamFailure.InvalidName => 159,
        _ => throw new InvalidOperationException("总力编队缺少明确的拒绝原因")
    };
}
