using System.Data.Common;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class BattlePassMissionHandler(TimeProvider? clock,
    Func<GameConnectionContext, BattlePassMissionState, InventoryCommitReceipt, Task> publish, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.BATTLE_PASS_REQUEST_MISSION, MissionAsync, nameof(BattlePassMissionHandler));

    async Task<CommandResult> MissionAsync(GameConnectionContext context, Packet packet)
    {
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var request = BattlePassRequests.Mission(packet.Body);
        BattlePassMissionSessionResult result = context.BlocksTownActions ? new(false, 19) :
            context.Session.ClaimBattlePassMission((clock ?? TimeProvider.System).GetUtcNow(), request.RawActionCode,
                request.RowKey, request.RawCategoryCode, request.RowIndex);
        bool Guard() => result.Receipt is { } receipt ? BattlePassHandler.SameOrigin(context, receipt) :
            context.Owner.Execute(() => context.Session.Selected?.Id == before.Selected?.Id &&
                context.Session.SelectionEpoch == before.SelectionEpoch && context.Session.Phase == before.Phase);
        log.Write("battle_pass_mission", new { Connection = context.Id, result.Accepted, result.Error,
            request.RawCategoryCode, request.RowKey, request.RowIndex });
        byte[] ack;
        if (result.Accepted)
        {
            // 本服策略：公共01后回显已验证的11B业务尾；opaque13B不参与账号、期段或经验授权。
            ack = new byte[12]; ack[0] = 1; packet.Body.AsSpan(13, 11).CopyTo(ack.AsSpan(1));
        }
        else ack = BattlePassHandler.Acknowledge(false, result.Error);
        try
        {
            await context.Reply(CmdPacketType.BATTLE_PASS_REQUEST_MISSION, ack,
                result.Accepted ? "battle_pass_mission_committed" : "battle_pass_mission_rejected", Guard);
            if (result.Accepted && result.State is { } state && result.Receipt is { } committed &&
                BattlePassHandler.SameOrigin(context, committed))
            {
                if (context.Session.CanPublishInventory(committed))
                {
                    await publish(context, state, committed);
                    if (context.Session.CanPublishInventory(committed)) return CommandResult.Handled;
                }
                while (true)
                {
                    context.Stop.ThrowIfCancellationRequested();
                    var current = context.Owner.Execute(() => BattlePassHandler.SameOriginLocked(context, committed)
                        ? context.Session.ReadBattlePassMissionState((clock ?? TimeProvider.System).GetUtcNow()) : null);
                    if (current is null) break;
                    await publish(context, current.State, current.Receipt);
                    if (context.Session.CanPublishInventory(current.Receipt)) break;
                    if (!BattlePassHandler.SameOrigin(context, committed)) break;
                    await Task.Yield();
                }
            }
        }
        catch (DbException error) when (result.Accepted)
        {
            throw new IOException("Committed battle pass mission publication failed", error);
        }
        return CommandResult.Handled;
    }
}
