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
        commands.Register(CmdPacketType.RAID_SET_SYMBOL, FacilityAsync, nameof(RaidCombatHandler), Game.Session.UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.RAID_MONSTER_HP, HpAsync, nameof(RaidCombatHandler), Game.Session.UserStateRequirement.DungeonContext);
    }

    async Task<CommandResult> FacilityAsync(GameConnectionContext context, Packet request)
    {
        if (context.Session.RaidCombatKind() == RaidKind.Anton && RaidRequests.AntonEnergyClear(request.Body) &&
            context.Session.PartyDungeon is { } antonParty && antonParty.ReportAntonEnergy(context.Session))
        {
            log.Write("raid_energy_confirmed", new { Connection = context.Id, Symbol = 110, Operand = 3000,
                Contract = "anton-cleared-run-last-v1" });
            context.Session.PublishRaidCombat();
            // Anton成功ACK的消费合同未闭合，只发布已提交583，不猜回包。
            return CommandResult.Handled;
        }
        if (!context.Session.AcceptsLukeFacilityReports || RaidRequests.LukeFacilitySymbol(request.Body) is not { } symbol || context.Session.PartyDungeon is not { } party)
            return CommandResult.Unhandled;
        // 忙锁重试先恢复原封存SQL；不能在回放同一662时重复执行设施增量。
        if (lifecycle is not null) context.Session.CommitPendingLukePhase(lifecycle);
        var receipt = party.ReportLukeFacility(context.Session, symbol, 1, 1);
        if (receipt is null) return CommandResult.Unhandled; // 失败错误码未知，不编造通用成功。
        if (lifecycle is not null) context.Session.CommitPendingLukePhase(lifecycle);
        if (receipt.Result.Outcome == LukeFacilityReportOutcome.Applied)
            log.Write("raid_facility_confirmed", new { Connection = context.Id, receipt.Battle, Run = receipt.Identity.InstanceId, Symbol = symbol });
        context.Session.PublishRaidCombat();
        // 静态028F85F6..028F8686的非零状态分支及缺省回调支持[01]；不推断失败正文。
        await context.Reply(CmdPacketType.RAID_SET_SYMBOL, [1], "luke_facility_confirmed", () => context.Session.CanPublishLukeFacility(receipt));
        return CommandResult.Handled;
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
        RaidRewardCardRequest selected;
        try { selected = RaidRequests.RewardCard(request.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        bool luke = context.Session.RaidCombatKind() == RaidKind.Luke;
        var entryView = luke ? context.Session.RaidCombatView() : null;
        var rejectedView = entryView;
        long epoch = context.Session.SelectionEpoch;
        Task<CommandResult> RejectLukeAsync()
        {
            // 02344BEE..02344C02：失败WORD 0x12清660等待；旧阶段拒绝不能清掉新窗口等待。
            bool Current() => rejectedView is not null && context.Session.SelectionEpoch == epoch &&
                context.Session.RaidCombatView() is { } current && current.Instance == rejectedView.Instance &&
                current.Phase == rejectedView.Phase && current.State == rejectedView.State;
            // 拒绝不是已提交资产，不使用QueuePublication仅保留会话的FIFO合同；泵写前重验阶段。
            return Task.FromResult(context.Sender.TryPost(1, (ushort)CmdPacketType.SELECT_RAID_REWARD_CARD,
                [0, 0x12, 0], "raid_reward_rejected", Current) ? CommandResult.Handled : CommandResult.CloseConnection);
        }
        if (luke && selected.Kind is not (0 or 1)) return await RejectLukeAsync();
        if (lifecycle is not null) context.Session.CommitPendingLukePhase(lifecycle);
        if (luke)
        {
            // 本请求可提交同阶段资格屏障；领取开始前冻结新窗口，领取后不得把旧失败重贴新阶段。
            var settled = context.Session.RaidCombatView();
            rejectedView = entryView is not null && settled is not null && context.Session.SelectionEpoch == epoch &&
                settled.Instance == entryView.Instance && settled.Phase == entryView.Phase &&
                (settled.State == entryView.State || entryView.State == AntonRaidBattleState.Fighting && settled.State == AntonRaidBattleState.Reward)
                ? settled : null;
        }
        var receipt = context.Session.ClaimRaidReward(selected.Kind, selected.Card, rewards, items, gold, pieces,
            (clock ?? TimeProvider.System).GetUtcNow());
        if (receipt is null)
        {
            log.Write("raid_reward_rejected", new { Connection = context.Id, selected.Kind, selected.Card,
                Reason = "reward-state-member-or-offer-unavailable" });
            // Anton失败字节未闭合；不能照搬Luke错误码或在未提交资产时回复成功。
            return luke ? await RejectLukeAsync() : CommandResult.Handled;
        }
        bool Guard() => context.Session.CanPublishRaidReward(receipt);
        log.Write("raid_reward_committed", new { Connection = context.Id, receipt.Prepared.Instance, receipt.Prepared.Phase,
            receipt.Prepared.Kind, receipt.Prepared.Card, receipt.Commit.Duplicate, Items = receipt.Prepared.Grants.Length,
            Mails = receipt.Commit.MailIds.Length });
        // 先提交资产，再刷新实际UID/邮件，随后发布同份冻结展示，最后确认660；成功ACK不携带物品。
        await inventory.RefreshAsync(context, receipt.Inventory);
        if (!receipt.Commit.MailIds.IsEmpty) context.Session.NotifyMailbox(receipt.Prepared.Character);
        // 只有完整显式原箱结果才使用Luke16B；无配置保留原领取能力，不补未知列。
        if (receipt.Prepared.RaidType == RaidKind.Luke &&
            LukeRewardResults.Bind(receipt, items, (clock ?? TimeProvider.System).GetUtcNow()) is { } result &&
            context.LukeResults.Post(context, receipt, result)) return CommandResult.Handled;
        // 600会写当前卡片窗口；旧阶段receipt只回放领取确认，不把旧展示装进新阶段。
        // 同阶段保护沿B线c0070b73；01DC8B70消费者及Docs/协议/副本.md翻牌章节。
        bool DisplayGuard() => Guard() && context.Session.RaidCombatView() is { State: AntonRaidBattleState.Reward } view &&
            view.Phase == receipt.Prepared.Phase;
        if (receipt.Prepared.RaidType == RaidKind.Anton && DisplayGuard())
            await context.Notify(NotiPacketType.RAID_REWARD_LIST, RaidCombatPackets.Reward(receipt.Prepared.Kind, receipt.Offer.Display),
                "raid_reward_display", DisplayGuard);
        if (receipt.Prepared.RaidType == RaidKind.Prey && DisplayGuard())
            await context.Notify(NotiPacketType.RAID_REWARD_LIST, PreyRewardPackets.Reward(receipt.Prepared.Character, receipt.Offer),
                "prey_reward_display", DisplayGuard);
        bool sent = false;
        await context.Reply(CmdPacketType.SELECT_RAID_REWARD_CARD, [1], "raid_reward_selected", () => sent = Guard());
        if (sent) context.Session.ConfirmRaidRewardPublished(receipt);
        return CommandResult.Handled;
    }
}
