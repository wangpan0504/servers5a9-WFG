using System.Buffers.Binary;
using System.Data.Common;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class BattlePassHandler(BattlePassRewardService rewards, string seasonKey,
    InventoryProjection inventory, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.BATTLE_PASS_REQUEST_REWARD, RewardAsync, nameof(BattlePassHandler));

    async Task<CommandResult> RewardAsync(GameConnectionContext context, Packet packet)
    {
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var request = BattlePassRequests.Reward(packet.Body);
        // 13B前缀保持不透明；账号及赛季不取自请求。
        BattlePassSessionReward result = context.BlocksTownActions ? new(InventoryResult.Reject(19)) :
            context.Session.ClaimBattlePassReward(rewards, seasonKey, request.RawActionCode, request.RawTrackCode, request.Key);
        var committed = result.Inventory;
        bool Guard() => committed.Receipt is { } receipt ? SameOrigin(context, receipt) :
            context.Owner.Execute(() => context.Session.Selected?.Id == before.Selected?.Id &&
                context.Session.SelectionEpoch == before.SelectionEpoch && context.Session.Phase == before.Phase);
        log.Write("battle_pass_reward", new { Connection = context.Id, committed.Accepted, committed.Error, committed.RejectReason });
        try
        {
            await context.Reply(CmdPacketType.BATTLE_PASS_REQUEST_REWARD, Acknowledge(committed.Accepted, committed.Error),
                committed.Accepted ? "battle_pass_reward_committed" : "battle_pass_reward_rejected", Guard);
            if (committed.Accepted && committed.Receipt is { } published && result.State is { } state &&
                committed.Plan is { } plan && SameOrigin(context, published))
            {
                if (context.Session.CanPublishInventory(published))
                {
                    await inventory.RefreshAsync(context, committed);
                    await BattlePassProjection.ClaimsAsync(context, state, published);
                    if (context.Session.CanPublishInventory(published)) return CommandResult.Handled;
                }
                while (true)
                {
                    context.Stop.ThrowIfCancellationRequested();
                    var current = context.Owner.Execute(() => SameOriginLocked(context, published)
                        ? context.Session.ReadBattlePass(seasonKey) : null);
                    if (current is null) break;
                    await inventory.ResynchronizeCommittedAsync(context, current.Receipt, published, plan);
                    await BattlePassProjection.ClaimsAsync(context, current.State, current.Receipt);
                    if (context.Session.CanPublishInventory(current.Receipt) && SameOrigin(context, published))
                    {
                        if (!plan.OverflowItems.IsEmpty) context.Session.NotifyMailbox(current.Receipt.Character.Id, restoreOnTown: true);
                        break;
                    }
                    if (!SameOrigin(context, published)) break;
                    await Task.Yield();
                }
            }
        }
        catch (DbException error) when (committed.Accepted)
        {
            throw new IOException("Committed battle pass reward publication failed", error);
        }
        return CommandResult.Handled;
    }

    internal static bool SameOrigin(GameConnectionContext context, InventoryCommitReceipt committed) =>
        context.Owner.Execute(() => SameOriginLocked(context, committed));

    internal static bool SameOriginLocked(GameConnectionContext context, InventoryCommitReceipt committed) =>
        context.Session.Ready && context.Owner.Generation == committed.Generation &&
        context.Session.SelectionEpoch == committed.SelectionEpoch && context.Session.Phase == committed.Phase &&
        context.Session.Selected?.Id == committed.Character.Id && context.Session.DungeonSnapshot?.Identity == committed.DungeonIdentity;

    // 使用公共ACK结构；本服务沿用库存失败码，不推断原版业务错误枚举。
    internal static byte[] Acknowledge(bool accepted, ushort error)
    {
        if (accepted) return [1];
        byte[] body = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), error);
        return body;
    }
}
