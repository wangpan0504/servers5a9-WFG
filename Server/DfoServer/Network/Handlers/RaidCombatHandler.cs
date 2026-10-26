using DfoServer.Game.Raids;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class RaidCombatHandler(IRaidRewardStore rewards, ItemCatalog items, GoldLimitCatalog gold,
    EpicPieceCatalog pieces, InventoryProjection inventory, ServerLog log, TimeProvider? clock = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SELECT_RAID_REWARD_CARD, RewardAsync, nameof(RaidCombatHandler), Game.Session.UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.RAID_MONSTER_HP, HpAsync, nameof(RaidCombatHandler), Game.Session.UserStateRequirement.DungeonContext);
    }

    Task<CommandResult> HpAsync(GameConnectionContext context, Packet request)
    {
        try { _ = RaidRequests.MonsterHp(request.Body); }
        catch (InvalidDataException) { return Task.FromResult(CommandResult.Unhandled); }
        // 原始HP字段的单位未闭合；不将客户端0当死亡，不回送猜测的634。
        return Task.FromResult(context.Session.RaidCombatView() is null ? CommandResult.Unhandled : CommandResult.Handled);
    }

    async Task<CommandResult> RewardAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 2) return CommandResult.Unhandled;
        var receipt = context.Session.ClaimRaidReward(request.Body[0], request.Body[1], rewards, items, gold, pieces,
            (clock ?? TimeProvider.System).GetUtcNow());
        if (receipt is null) return CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishRaidReward(receipt);
        log.Write("raid_reward_committed", new { Connection = context.Id, receipt.Prepared.Instance, receipt.Prepared.Phase,
            receipt.Prepared.Kind, receipt.Prepared.Card, receipt.Commit.Duplicate, Items = receipt.Prepared.Grants.Length,
            Mails = receipt.Commit.MailIds.Length });
        // 先提交资产，再刷新实际UID/邮件，随后发布同份冻结展示，最后确认660；成功ACK不携带物品。
        await inventory.RefreshAsync(context, receipt.Inventory);
        if (!receipt.Commit.MailIds.IsEmpty) context.Session.NotifyMailbox(receipt.Prepared.Character);
        await context.Notify(NotiPacketType.RAID_REWARD_LIST, RaidCombatPackets.Reward(receipt.Prepared.Kind, receipt.Offer.Display),
            "raid_reward_display", Guard);
        bool sent = false;
        await context.Reply(CmdPacketType.SELECT_RAID_REWARD_CARD, [1], "raid_reward_selected", () => sent = Guard());
        if (sent) context.Session.ConfirmRaidRewardPublished(receipt);
        return CommandResult.Handled;
    }
}
