using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class GuildContributionProjection(GuildContributionService service, InventoryProjection inventory, ServerLog log,
    GuildHongbaoService? hongbao = null)
{
    public async Task ReadyAsync(GameConnectionContext context)
    {
        GuildContributionUseResult? use = null;
        try
        {
            var observed = service.Observe(context.Session, GuildContributionSource.Login);
            Record(context, "login", observed);
            use = context.Session.SettleGuildContribution(service);
            Record(context, "weekly", use.Commit);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
        {
            log.Write("guild_contribution_error", new { Connection = context.Id, Source = "ready", Error = error.ToString() });
        }
        // 登陆贡献不额外回放整个公会面板；真实到账后先刷新资产，最后才展示获得提示。
        if (use is not null && use.Inventory is not null && use.Commit.Coins > 0 && use.IsCurrent is not null)
        {
            await inventory.RefreshAsync(context, use.Inventory, refreshEquipment: false);
            byte[] count = BitConverter.GetBytes(checked((uint)use.Commit.Coins));
            await context.Notify(NotiPacketType.GUILD_MEMBER_COIN, count, "guild_weekly_coins", use.IsCurrent);
        }
        if (hongbao is not null)
        {
            var refund = context.Session.UseGuildHongbao(hongbao, GuildHongbaoAction.Refund);
            if (refund.Inventory is not null)
            {
                await inventory.RefreshAsync(context, refund.Inventory, refreshEquipment: false);
                log.Write("guild_hongbao", new { Connection = context.Id, Action = "Refund", refund.Commit.Amount, refund.Commit.Error });
            }
        }
    }

    public void Chat(GameConnectionContext context)
    {
        try { Record(context, "chat", service.Observe(context.Session, GuildContributionSource.Chat)); }
        catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
        { log.Write("guild_contribution_error", new { Connection = context.Id, Source = "chat", Error = error.ToString() }); }
    }

    void Record(GameConnectionContext context, string source, GuildContributionCommit result)
    {
        if (result.Points == 0 && result.Coins == 0 && result.Error is 0 or 3 or 34) return;
        log.Write("guild_contribution", new { Connection = context.Id, Source = source, Guild = result.GuildId,
            result.Error, result.Points, result.Coins, result.Revision });
    }
}
