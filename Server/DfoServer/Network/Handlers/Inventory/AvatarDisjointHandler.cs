using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class AvatarDisjointHandler(InventoryAvatarDisjointService disjoint, InventoryProjection projection,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.DISJOINT_AVATAR, Handle, nameof(AvatarDisjointHandler));
        commands.Register(CmdPacketType.AVATAR_DISJOINT_RANDOM_REWARD, Handle, nameof(AvatarDisjointHandler));
    }

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var opcode = (CmdPacketType)packet.Opcode;
        bool randomReward = opcode == CmdPacketType.AVATAR_DISJOINT_RANDOM_REWARD;
        var original = context.Owner.Execute(() =>
            (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool SameSelection() => context.Owner.Execute(() =>
            context.Session.SelectionEpoch == original.SelectionEpoch && context.Session.Phase == original.Phase &&
            ReferenceEquals(context.Session.Selected, original.Selected));
        AvatarDisjointRequest request;
        try
        {
            request = AvatarDisjointRequests.Read(packet.Body);
        }
        catch (InvalidDataException)
        {
            log.Write("avatar_disjoint", new
            {
                Connection = context.Id,
                Mode = randomReward ? "random-reward" : "standard",
                Length = packet.Body.Length,
                Accepted = false,
                Reason = "malformed"
            });
            await context.Reply(opcode, CargoPackets.Ack(InventoryResult.Reject(19)),
                "avatar_disjoint_malformed", SameSelection);
            return CommandResult.Handled;
        }

        AvatarDisjointReceipt? receipt = null;
        string? rejection = null;
        var result = context.BlocksTownActions
            ? InventoryResult.Reject(19)
            : context.Session.DisjointAvatar(request, disjoint, out receipt, out rejection);
        rejection ??= result.Accepted ? null : "blocked-state";
        bool CanSend() => result.Receipt is { } committed
            ? context.Session.CanPublishInventory(committed)
            : SameSelection();
        log.Write("avatar_disjoint", new
        {
            Connection = context.Id,
            Mode = randomReward ? "random-reward" : "standard",
            request.SourceSlot,
            request.ExpectedItemId,
            result.Accepted,
            result.Error,
            Reason = result.Accepted ? "committed" : rejection,
            Revision = result.Receipt?.Character.Revision,
            Rewards = receipt?.Rewards.Length
        });
        byte[] acknowledgement = randomReward
            ? AvatarDisjointPackets.RandomRewardAck(result, receipt)
            : AvatarDisjointPackets.StandardAck(result, receipt);
        await context.Reply(opcode, acknowledgement,
            result.Accepted ? "avatar_disjoint_committed" : "avatar_disjoint_rejected", CanSend);
        try
        {
            await projection.RefreshAsync(context, result);
            if (result.Accepted && result.Plan?.AvatarDisjoint is { } fact && CanSend())
                await DfoServer.Network.Projection.BattlePassMissionProjection.RefreshAsync(context, fact.Clock.GetUtcNow());
        }
        catch (System.Data.Common.DbException error) when (result.Plan?.AvatarDisjoint is not null)
        {
            // 原装扮与随机徽章已提交，发布失败不能重抽或再次分解。
            throw new IOException("Committed avatar disjoint publication failed", error);
        }
        return CommandResult.Handled;
    }
}
