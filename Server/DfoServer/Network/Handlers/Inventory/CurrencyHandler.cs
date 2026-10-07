using DfoServer.Game.Currency;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class CurrencyHandler(CurrencyService service, InventoryProjection projection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.CERA, QueryAsync, nameof(CurrencyHandler));
        commands.Register(CmdPacketType.UPGRADE_CARRY_GOLD, UpgradeAsync, nameof(CurrencyHandler));
    }
    static async Task<CommandResult> QueryAsync(GameConnectionContext context, Packet request)
    {
        if (!CurrencyRequests.IsCeraQuery(request.Body)) return CommandResult.Unhandled;
        await context.Notify(NotiPacketType.CERA, CurrencyPackets.Cera(context.Session.AccountWallet()), "account_cera");
        return CommandResult.Handled;
    }
    async Task<CommandResult> UpgradeAsync(GameConnectionContext context, Packet request)
    {
        byte stage = CurrencyRequests.GoldLimitUpgrade(request.Body);
        var result = context.BlocksTownActions ? DfoServer.Game.Inventory.Models.InventoryResult.Reject() :
            context.Session.MutateInventory(character => service.Upgrade(character, stage));
        var current = context.Owner.Execute(() => context.Session.Selected);
        if (current is null) return CommandResult.Handled;
        // 失败只同步权威阶段；不套用其他版本尚未核实的错误码。
        await context.Notify(NotiPacketType.UPGRADE_CARRY_GOLD, CurrencyPackets.GoldLimit(current.Inventory), "gold_limit");
        if (result.Accepted)
        {
            await context.Reply(CmdPacketType.UPGRADE_CARRY_GOLD, CurrencyPackets.GoldLimitUpgraded(current.Inventory), "gold_limit_upgraded");
            await projection.RefreshAsync(context, result);
        }
        return CommandResult.Handled;
    }
}
