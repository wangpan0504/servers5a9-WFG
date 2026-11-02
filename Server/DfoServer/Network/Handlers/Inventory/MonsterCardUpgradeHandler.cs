using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class MonsterCardUpgradeHandler(InventoryMonsterCardUpgradeService upgrades,
    InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.UPGRADE_CARD, Handle, nameof(MonsterCardUpgradeHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        MonsterCardUpgradeRequest request;
        try { request = MonsterCardUpgradeRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("monster_card_upgrade", new { Connection = context.Id, Accepted = false, Reason = "malformed", Length = packet.Body.Length });
            await context.Reply(CmdPacketType.UPGRADE_CARD, MonsterCardUpgradePackets.Failure(), "monster_card_upgrade_malformed");
            return CommandResult.Handled;
        }
        var original = context.Owner.Execute(() =>
            (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        MonsterCardUpgradeReceipt? receipt = null;
        string? rejection = null;
        var result = context.BlocksTownActions ? InventoryResult.Reject(22) :
            context.Session.UpgradeMonsterCard(request, upgrades, out receipt, out rejection);
        bool CanSend() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) :
            context.Owner.Execute(() => context.Session.SelectionEpoch == original.SelectionEpoch &&
                context.Session.Phase == original.Phase && ReferenceEquals(context.Session.Selected, original.Selected));
        log.Write("monster_card_upgrade", new
        {
            Connection = context.Id, result.Accepted, result.Error,
            Reason = result.Accepted ? "committed" : rejection ?? "blocked-state",
            Target = request.TargetSlot, Materials = request.MaterialSlots.Length,
            receipt?.ItemId, receipt?.Level, receipt?.Upgraded, Revision = result.Receipt?.Character.Revision
        });
        if (!result.Accepted)
        {
            await context.Reply(CmdPacketType.UPGRADE_CARD, MonsterCardUpgradePackets.Failure(), "monster_card_upgrade_rejected", CanSend);
            return CommandResult.Handled;
        }
        if (receipt is null)
            throw new InvalidOperationException("卡片升级提交后缺少业务回执");
        await context.Reply(CmdPacketType.UPGRADE_CARD, MonsterCardUpgradePackets.Success(receipt), "monster_card_upgrade_committed", CanSend);
        try
        {
            await projection.RefreshAsync(context, result);
            if (result.Plan?.MonsterCardUpgrade is { } fact && CanSend())
                await DfoServer.Network.Projection.BattlePassMissionProjection.RefreshAsync(context, fact.Clock.GetUtcNow());
        }
        catch (System.Data.Common.DbException error) when (result.Plan?.MonsterCardUpgrade is not null)
        {
            // 原升级与材料已提交，后置读取失败不能重复发起升级或消费材料。
            throw new IOException("Committed monster card upgrade publication failed", error);
        }
        return CommandResult.Handled;
    }
}
