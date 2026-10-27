using DfoServer.Game.Raids;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class RaidCombatHandler(IRaidRewardStore rewards, ItemCatalog items, GoldLimitCatalog gold,
    EpicPieceCatalog pieces, InventoryProjection inventory, ServerLog log, TimeProvider? clock = null, ILukeRaidLifecycleStore? lifecycle = null) : IGameCommandModule
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
        bool luke = context.Session.RaidCombatKind() == RaidKind.Luke;
        var rejectedView = luke ? context.Session.RaidCombatView() : null;
        long epoch = context.Session.SelectionEpoch;
        async Task<CommandResult> RejectLukeAsync()
        {
            // 02344BEE..02344C02：失败WORD 0x12清660等待；不把不支持请求留成永久pending。
            await context.Reply(CmdPacketType.SELECT_RAID_REWARD_CARD, [0, 0x12, 0], "raid_reward_rejected",
                () => rejectedView is not null && context.Session.SelectionEpoch == epoch && context.Session.RaidCombatView()?.Instance == rejectedView.Instance);
            return CommandResult.Handled;
        }
        if (luke && request.Body[0] is not (0 or 1)) return await RejectLukeAsync();
        if (lifecycle is not null) context.Session.CommitPendingLukePhase(lifecycle);
        var receipt = context.Session.ClaimRaidReward(request.Body[0], request.Body[1], rewards, items, gold, pieces,
            (clock ?? TimeProvider.System).GetUtcNow());
        if (receipt is null) return luke ? await RejectLukeAsync() : CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishRaidReward(receipt);
        log.Write("raid_reward_committed", new { Connection = context.Id, receipt.Prepared.Instance, receipt.Prepared.Phase,
            receipt.Prepared.Kind, receipt.Prepared.Card, receipt.Commit.Duplicate, Items = receipt.Prepared.Grants.Length,
            Mails = receipt.Commit.MailIds.Length });
        // 先提交资产，再刷新实际UID/邮件，随后发布同份冻结展示，最后确认660；成功ACK不携带物品。
        await inventory.RefreshAsync(context, receipt.Inventory);
        if (!receipt.Commit.MailIds.IsEmpty) context.Session.NotifyMailbox(receipt.Prepared.Character);
        // Luke仅已证请求事务能力：600通用尾两列未解释，不能猜0发送展示；没有普通生产配置入口。
        if (receipt.Prepared.RaidType == RaidKind.Anton)
            await context.Notify(NotiPacketType.RAID_REWARD_LIST, RaidCombatPackets.Reward(receipt.Prepared.Kind, receipt.Offer.Display),
                "raid_reward_display", Guard);
        bool sent = false;
        await context.Reply(CmdPacketType.SELECT_RAID_REWARD_CARD, [1], "raid_reward_selected", () => sent = Guard());
        if (sent) context.Session.ConfirmRaidRewardPublished(receipt);
        return CommandResult.Handled;
    }
}
