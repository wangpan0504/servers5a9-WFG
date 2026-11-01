using DfoServer.Game.PrivilegeServices;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class PrivilegeServiceHandler(PrivilegeServiceRewards rewards, InventoryProjection inventory, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.PRIVILEGE_SERVICE_LOGIN_REWARD,
        RewardAsync, nameof(PrivilegeServiceHandler));

    async Task<CommandResult> RewardAsync(GameConnectionContext context, Packet packet)
    {
        PrivilegeRewardRequest request;
        try { request = PrivilegeServiceRequests.Reward(packet.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        if (context.Session.ReadInventoryForSynchronization() is not { } expected) return CommandResult.Unhandled;
        var result = context.Session.ClaimPrivilegeReward(rewards, request.Mode, request.Value);
        log.Write("privilege_login_reward", new { Connection = context.Id, request.Mode, request.Value,
            result.Accepted, result.Error, result.RejectReason, Revision = result.Receipt?.Character.Revision });
        if (!result.Accepted)
        {
            // 只使用已核证的背包空间失败3；未核证的其他错误码不伪造为成功。
            if (result.RejectReason == "privilege_reward_capacity")
                await context.Reply(CmdPacketType.PRIVILEGE_SERVICE_LOGIN_REWARD, PrivilegeServicePackets.RewardCapacityRejected(),
                    "privilege_reward_capacity", () => context.Session.CanPublishInventory(expected));
            await PrivilegeServiceProjection.PostAsync(context, expected);
            return CommandResult.Handled;
        }
        var receipt = result.Receipt!;
        bool Current() => context.Session.CanPublishInventory(receipt);
        using var publication = context.Sender.QueuePublication(Current);
        await inventory.RefreshAsync(context, result);
        await context.Reply(CmdPacketType.PRIVILEGE_SERVICE_LOGIN_REWARD, PrivilegeServicePackets.RewardCommitted(), "privilege_reward_committed", Current);
        return CommandResult.Handled;
    }
}
