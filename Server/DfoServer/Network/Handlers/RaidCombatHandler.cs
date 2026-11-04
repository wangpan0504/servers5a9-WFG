using DfoServer.Game.Raids;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class RaidCombatHandler(IRaidRewardStore rewards, ItemCatalog items, GoldLimitCatalog gold,
    EpicPieceCatalog pieces, InventoryProjection inventory, ServerLog log, TimeProvider? clock = null, ILukeRaidLifecycleStore? lifecycle = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SELECT_RAID_REWARD_CARD, RewardAsync, nameof(RaidCombatHandler), Game.Session.UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.RAID_MOVIE_SKIP, MovieAsync, nameof(RaidCombatHandler), Game.Session.UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.RAID_SET_SYMBOL, FacilityAsync, nameof(RaidCombatHandler), Game.Session.UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.RAID_MONSTER_HP, HpAsync, nameof(RaidCombatHandler), Game.Session.UserStateRequirement.DungeonContext);
    }

    Task<CommandResult> MovieAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 1 || request.Body[0] > 1) return Task.FromResult(CommandResult.Unhandled);
        if (lifecycle is not null) context.Session.CommitPendingLukePhase(lifecycle);
        if (context.Session.RaidMovieSnapshot() is not { } movie) return Task.FromResult(CommandResult.Unhandled);
        return Task.FromResult(RaidCombatProjection.Movie(context, movie.Actor, movie.Battle, movie.View, request.Body[0] == 1)
            ? CommandResult.Handled : CommandResult.CloseConnection);
    }

    async Task<CommandResult> FacilityAsync(GameConnectionContext context, Packet request)
    {
        if (context.Session.RaidCombatKind() == RaidKind.Anton && RaidRequests.AntonConstructorLast(request.Body) is { } constructor &&
            context.Session.PartyDungeon is { } constructorParty)
        {
            var report = constructorParty.ReportAntonConstructorLast(context.Session, constructor);
            if (report.EnergyChanged) context.Session.PublishRaidCombat();
            return report.Accepted ? CommandResult.Handled : CommandResult.Unhandled;
        }
        if (context.Session.RaidCombatKind() == RaidKind.Anton && RaidRequests.AntonBatteryLast(request.Body) &&
            context.Session.PartyDungeon is { } batteryParty)
        {
            var report = batteryParty.ReportAntonObjectLast(context.Session);
            if (report.EnergyChanged) context.Session.PublishRaidCombat();
            // 662本身不携带对象身份；只有批准生成、23销毁和此Last三者齐备才扣能量。
            return report.Accepted ? CommandResult.Handled : CommandResult.Unhandled;
        }
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

    async Task<CommandResult> HpAsync(GameConnectionContext context, Packet request)
    {
        RaidMonsterHpRequest report;
        try { report = RaidRequests.MonsterHp(request.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        if (context.Session.RaidCombatKind() is RaidKind.Anton or RaidKind.Luke or RaidKind.Prey && context.Session.PartyDungeon is { } party)
        {
            var notices = party.ReportAntonHp(context.Session, report.RawValue, report.RawEntries);
            var own = notices.FirstOrDefault(notice => ReferenceEquals(notice.Recipient.Actor.Session, context.Session));
            if (own is { Scene: { } scene, AntonHp: { } display } && context.Session.ReportRaidStatusHp(scene, display) is { } change)
                context.Session.PublishRaidCombat(change);
            else await DfoServer.Network.Projection.PartyDungeonProjection.PublishAsync(notices);
        }
        return context.Session.RaidCombatView() is null ? CommandResult.Unhandled : CommandResult.Handled;
    }

    async Task<CommandResult> RewardAsync(GameConnectionContext context, Packet request)
    {
        RaidRewardCardRequest selected;
        try { selected = RaidRequests.RewardCard(request.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        if (selected.Kind == 0 && context.Session.SelectAntonEmptyCardBack(selected.Card) is { } back)
        {
            bool BackCurrent() => context.Session.CanPublishRaidBattle(back.Actor, back.View);
            bool AcknowledgeCurrent() => back.Actor.IsCurrent &&
                context.Session.SelectAntonEmptyCardBack(selected.Card) is { } latest &&
                latest.Actor == back.Actor && latest.View.Instance == back.View.Instance &&
                latest.View.Phase == back.View.Phase && latest.Card == back.Card &&
                latest.View.State is AntonRaidBattleState.Reward or AntonRaidBattleState.Intermission or AntonRaidBattleState.Completed;
            // 自然客户端同一次选择连续发送1/0；0只确认牌背，不再借它发放全团奖池。
            if (back.View.State == AntonRaidBattleState.Reward)
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_REWARD_LIST, AntonRewardPackets.Choices(0), "anton_card_back", BackCurrent);
            context.Sender.Post(1, (ushort)CmdPacketType.SELECT_RAID_REWARD_CARD, [1], "anton_card_back_selected", AcknowledgeCurrent);
            return CommandResult.Handled;
        }
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
        // LukeResults成功会提前return，任务快照必须沿原已提交资产发布点刷新。
        if (DfoServer.Game.BattlePass.BattlePassUndefeatedSongSource.DeclaresSong(receipt.Prepared) && Guard())
            await DfoServer.Network.Projection.BattlePassMissionProjection.RefreshAsync(context, (clock ?? TimeProvider.System).GetUtcNow());
        // 只有完整显式原箱结果才使用Luke16B；无配置保留原领取能力，不补未知列。
        if (receipt.Prepared.RaidType == RaidKind.Luke &&
            LukeRewardResults.Bind(receipt, items, (clock ?? TimeProvider.System).GetUtcNow()) is { } result &&
            context.LukeResults.Post(context, receipt, result)) return CommandResult.Handled;
        // 600会写当前卡片窗口；旧阶段receipt只回放领取确认，不把旧展示装进新阶段。
        bool DisplayGuard() => Guard() && context.Session.RaidCombatView() is { State: AntonRaidBattleState.Reward } view &&
            view.Phase == receipt.Prepared.Phase;
        // 普通20人安徒恩走023CA1B0公共16B结果；01DC8B70的8B分支只属于+DC非零的单人/引导记录。
        if (receipt.Prepared.RaidType == RaidKind.Anton && DisplayGuard() &&
            context.Session.AntonRaidTimerSnapshot() is null)
            await context.Notify(NotiPacketType.RAID_REWARD_LIST, RaidCombatPackets.Reward(receipt.Prepared.Kind, receipt.Offer.Display),
                "raid_reward_display", DisplayGuard);
        if (receipt.Prepared.RaidType == RaidKind.Anton &&
            context.Session.AntonRaidTimerSnapshot() is not null)
        {
            if (AntonRewardPackets.TryFrozenResult(receipt.Prepared.Kind, receipt.Prepared.Character, receipt.Prepared.Card,
                receipt.Prepared.Grants, out var body, out var missing))
                await context.Notify(NotiPacketType.RAID_REWARD_LIST, body, "raid_reward_display", DisplayGuard);
            else
                log.Write("raid_reward_display_rejected", new { Connection = context.Id, receipt.Prepared.Phase,
                    receipt.Prepared.Kind, Reason = missing });
        }
        if (receipt.Prepared.RaidType is RaidKind.Prey or RaidKind.Fiendwar && DisplayGuard())
            await context.Notify(NotiPacketType.RAID_REWARD_LIST, PreyRewardPackets.Reward(receipt.Prepared.Character, receipt.Offer, receipt.Prepared.RaidType),
                "prey_reward_display", DisplayGuard);
        bool sent = false;
        await context.Reply(CmdPacketType.SELECT_RAID_REWARD_CARD, [1], "raid_reward_selected", () => sent = Guard());
        if (sent)
        {
            context.Session.ConfirmRaidRewardPublished(receipt);
            if (receipt.Prepared.RaidType == RaidKind.Anton && receipt.Prepared.Kind is 1 or 2)
                await PublishAutomaticAntonSquadAsync(context);
        }
        return CommandResult.Handled;
    }

    async Task PublishAutomaticAntonSquadAsync(GameConnectionContext context)
    {
        var receipt = context.Session.ClaimAutomaticAntonSquadReward(rewards, items, gold, pieces,
            (clock ?? TimeProvider.System).GetUtcNow());
        if (receipt is null) return;
        await inventory.RefreshAsync(context, receipt.Inventory);
        if (!receipt.Commit.MailIds.IsEmpty) context.Session.NotifyMailbox(receipt.Prepared.Character);
        // 完整团队权益已在state4前一次装配；本人到账只刷新库存，不能用单行600覆盖全表。
        context.Session.ConfirmRaidRewardPublished(receipt);
        log.Write("raid_reward_committed", new { Connection = context.Id, receipt.Prepared.Instance,
            receipt.Prepared.Phase, receipt.Prepared.Kind, receipt.Prepared.Card, receipt.Commit.Duplicate, Automatic = true });
    }
}
