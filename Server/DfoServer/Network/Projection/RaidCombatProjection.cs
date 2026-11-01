using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed record RaidCombatDisplayIdentity(Game.Session.GameSession Session, Game.Accounts.AccountOwners.Lease Owner,
    long SelectionEpoch, ushort Character, Guid Instance, int Phase, AntonRaidBattleState State, bool AntonPreparing)
{
    public static RaidCombatDisplayIdentity Capture(RaidActor actor, AntonRaidBattleView view) =>
        new(actor.Session, actor.Owner, actor.Epoch, actor.Character, view.Instance, view.Phase, view.State, view.AntonPreparing || view.LukePreparing);
}

internal static partial class RaidCombatProjection
{
    public static void Notice(GameConnectionContext context, AntonRaidBattleView view, RaidActor actor)
    {
        bool Guard() => context.Session.CanPublishRaidBattle(actor, view);
        // 卢克首个Run策略尚无奖励记录，不能借安徒恩投影打开空窗口。
        if (view.State == AntonRaidBattleState.Reward && context.Session.CurrentRaidBattle(actor)?.Kind == RaidKind.Luke &&
            context.Session.RaidCombatResult(view) is null) return;
        if (context.Session.CurrentRaidBattle(actor) is { Kind: RaidKind.Luke } luke)
            PostLukeSnapshot(context, luke, view, Guard);
        else if (context.Session.CurrentRaidBattle(actor) is { Kind: RaidKind.Prey } prey)
            PostPreySnapshot(context, prey, view, Guard);
        else if (context.Session.CurrentRaidBattle(actor) is { Kind: RaidKind.Fiendwar } fiendwar)
        {
            PostFiendwarSnapshot(context, fiendwar, view, Guard);
            context.LukePhaseExit?.Notice(view, actor);
        }
        else if (context.Session.CurrentRaidBattle(actor) is { Kind: RaidKind.Anton } anton)
        {
            // DGN HP与资源SET共享原symbol快照；沿已证583读宽同步本次提交。
            foreach (var row in anton.DungeonSnapshot(view))
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_CHANGE_DUNGEON_STATE,
                    RaidCombatPackets.DungeonState(row.Id, 0, row.State), "raid_dungeon_state",
                    () => Guard() && DungeonChanged(context, view.Instance, row.Id, row.State),
                    () => RecordAntonDungeon(context, view.Instance, row.Id, row.State));
            if (anton.HasAntonRewards || anton.PhaseProgress)
                PostAntonSnapshot(context, anton, view, Guard);
        }
        context.LukePhaseExit?.Notice(view, actor);
        if (context.Session.CurrentRaidBattle(actor) is { Kind: RaidKind.Anton or RaidKind.Luke or RaidKind.Prey } statusBattle)
        {
            PostStatus(context, statusBattle, view, Guard);
            PostRetryEligibility(context, Guard);
        }
        if (view.PhaseExpired)
        {
            return;
        }
        PostState(context, view, actor, Guard, "raid_phase_state");
        PostRoster(context, view, actor, Guard);
        if (view.State is AntonRaidBattleState.Reward or AntonRaidBattleState.Completed)
            RaidClearProjection.Refresh(context, Guard);
    }

    static void PostRetryEligibility(GameConnectionContext context, Func<bool> current)
    {
        if (context.Session.RaidDungeonResultFocus() is not { } receipt) return;
        bool available = context.Session.CanRetryRaidResultDungeon();
        var display = (Run: receipt.Snapshot.Identity.InstanceId, Available: available);
        // 261只控制按钮资格。必须有全分队已提交的清图事实，不能借HP0、战况Clear或普通结果页推定。
        context.Sender.Post(0, (ushort)NotiPacketType.EPLP_RECHALLENGE,
            DungeonSettlementPackets.RetryEligibility(available), "raid_result_retry_eligibility",
            () => current() && context.Session.CanPublishDungeonSettlement(receipt) &&
                context.Session.RaidDungeonResultFocus()?.Snapshot.Identity.InstanceId == display.Run &&
                context.Session.CanRetryRaidResultDungeon() == available && context.RaidRetryDisplay != display,
            () => context.RaidRetryDisplay = display);
    }

    static void PostRoster(GameConnectionContext context, AntonRaidBattleView view, RaidActor actor, Func<bool> current)
    {
        context.Owner.Execute(() =>
        {
            if (!current() || context.Session.CaptureRaidRoster(actor) is not { Started: { } started } party ||
                started.InstanceId != view.Instance || context.Session.CurrentRaidBattle(actor)?.RosterClears(view) is not { } clears) return false;
            // 02349A20的mode2同时替换实时阶段与成员表，不依赖pending、不打开窗口。
            // 591/op3会强制开窗，仅保留给原有建队、编队流程。
            var body = RaidPackets.Information(2, party.Key, party.TitleBytes, party.Type, party.Leader,
                DfoServer.Game.Characters.CharacterName.Encoding.GetBytes(party.LeaderName), RaidProjection.Members(party),
                (byte)view.State, checked((byte)view.Phase), guide: party.Guide, clearCounts: clears, recruitRoles: party.RecruitRoles);
            bool Guard() => current() && actor.Session.HasRaidKey(actor, party.Key) &&
                (context.RaidRosterDisplay is not { } displayed || !body.AsSpan().SequenceEqual(displayed));
            context.Sender.Post(1, (ushort)CmdPacketType.REQUEST_RAID_INFO, body, "raid_clear_counts", Guard,
                () => context.RaidRosterDisplay = body);
            return true;
        });
    }

    internal static void PostAntonSnapshot(GameConnectionContext context, AntonRaidBattle battle,
        AntonRaidBattleView view, Func<bool> current)
    {
        if (battle.Kind != RaidKind.Anton || view.Instance != battle.Instance ||
            !view.Symbols.TryGetValue("[is exist infection dungeon]", out int infected) ||
            !view.Symbols.TryGetValue("[infection dungeon index]", out int infectionDungeon)) return;
        bool Guard() => current() && battle.IsCurrent(view);
        // 585尾值保留感染身份；01DCF210会对非零身份置symbol7=1，随后583恢复本次真实开闭值及剩余HP。
        IReadOnlyList<(int Id, GameWorld.Raids.RaidDungeonState State)> published = [];
        // 585与586共用01DD9DD0，重复Clear也会弹横幅；在FIFO实际写帧时过滤。
        context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_DUNGEON_STATE,
            6 + battle.DungeonSnapshot(view).Length * 5,
            () =>
            {
                published = battle.DungeonSnapshot(view).Where(row =>
                    DungeonChanged(context, view.Instance, row.Id, row.State)).ToArray();
                return RaidCombatPackets.Dungeons(0, published, infectionDungeon);
            }, "raid_dungeon_snapshot", Guard, allowShorter: true, written: () =>
            {
                foreach (var row in published) RecordAntonDungeon(context, view.Instance, row.Id, row.State);
            });
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_SET_SYMBOL,
            RaidCombatPackets.Symbols(battle.SymbolSnapshot(view)), "raid_infection_state", Guard);
        PostAntonTimers(context, battle, view, Guard);
    }

    static bool DungeonChanged(GameConnectionContext context, Guid instance, int dungeon,
        GameWorld.Raids.RaidDungeonState state) => context.AntonDungeonDisplayInstance != instance ||
        !context.AntonDungeonDisplays.TryGetValue(dungeon, out var previous) || previous != state;

    static void RecordAntonDungeon(GameConnectionContext context, Guid instance, int dungeon,
        GameWorld.Raids.RaidDungeonState state)
    {
        if (context.AntonDungeonDisplayInstance != instance)
        {
            context.AntonDungeonDisplays.Clear();
            context.AntonDungeonDisplayInstance = instance;
        }
        context.AntonDungeonDisplays[dungeon] = state;
    }

    internal static void Selection(GameConnectionContext context, Func<bool> current)
    {
        if (context.Session.AntonRaidMap() is not { } map) return;
        var view = map.Battle.View;
        PostAntonSnapshot(context, map.Battle, view, () => current() &&
            context.Session.StartedRaid?.InstanceId == view.Instance && context.Session.CanPublishRaidBattle(map.Actor, view));
        if (view.State == AntonRaidBattleState.Fighting && map.Battle.HasAntonRewards)
            PostState(context, view, map.Actor, () => current() && context.Session.CanPublishRaidBattle(map.Actor, view), "anton_selection_phase_state");
    }

    internal static void PostState(GameConnectionContext context, AntonRaidBattleView view, RaidActor actor,
        Func<bool> current, string action)
    {
        if (context.Session.CurrentRaidBattle(actor)?.Kind == RaidKind.Fiendwar)
        {
            PostFiendwarState(context, view, actor, current);
            return;
        }
        if (view.PhaseExpired) return;
        context.Owner.Execute(() =>
        {
            var identity = RaidCombatDisplayIdentity.Capture(actor, view);
            bool SceneReady() => view.State != AntonRaidBattleState.Reward ||
                context.Session.CurrentRaidBattle(actor)?.Kind != RaidKind.Luke ||
                context.Session.Phase == Game.Session.SessionPhase.Town;
            if (!SceneReady()) return false;
            bool Show() => current() && SceneReady() && context.RaidDisplay != identity;
            // 587同state/phase仍会重新安装原生动画；601只判断，587成功写帧才确认。
            // 两包在本人owner内连续入队，无网络等待；仅使用Post，不用于QueuePublished的提前guard。
            // 601使用资格提交后冻结的真实阶段统计。
            if (view.State == AntonRaidBattleState.Reward && context.Session.RaidCombatResult(view) is { } result)
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_RESULT, RaidCombatPackets.Result(result), "raid_phase_result", Show);
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_STATE, RaidCombatPackets.State(view), action, Show, () =>
            {
                if (actor.IsCurrent && context.Session.CurrentRaidBattle(actor)?.Instance == view.Instance)
                    context.RaidDisplay = identity;
            });
            if (view.State == AntonRaidBattleState.Reward &&
                context.Session.CurrentRaidBattle(actor) is { Kind: RaidKind.Prey } prey &&
                prey.LukeEligibility(actor.Character, view.Phase) is { Eligible: true } &&
                prey.RewardPolicy(actor, view.Phase) is { } preyPolicy)
            {
                bool Cards() => current() && context.RaidDisplay == identity && context.AntonCardsDisplay != identity;
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_MOVIE_SKIP, [], "prey_movie_skip", Cards);
                foreach (var offer in preyPolicy.Offers)
                    context.Sender.Post(0, (ushort)NotiPacketType.RAID_REWARD_LIST,
                        PreyRewardPackets.Reward(actor.Character, offer), "prey_card_list", Cards,
                        offer == preyPolicy.Offers[^1] ? () => context.AntonCardsDisplay = identity : null);
            }
            if (view.State == AntonRaidBattleState.Reward && view.AntonCardsReady &&
                context.Session.CurrentRaidBattle(actor) is { HasAntonRewards: true } rewardBattle &&
                rewardBattle.RewardPolicy(actor, view.Phase) is { } policy &&
                rewardBattle.AntonEligibility(actor.Character, view.Phase) is { Eligible: true })
            {
                bool Cards() => current() && context.RaidDisplay == identity && context.AntonCardsDisplay != identity;
                // 023A7220不读包体；599仅切换演出。沿同一FIFO先599再两类600。
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_MOVIE_SKIP, [], "anton_movie_skip", Cards);
                foreach (var offer in policy.Offers)
                    context.Sender.Post(0, (ushort)NotiPacketType.RAID_REWARD_LIST,
                        RaidCombatPackets.Reward(offer.Kind, offer.Display), "anton_card_list", Cards, offer == policy.Offers[^1] ? () => context.AntonCardsDisplay = identity : null);
            }
            if (view.State == AntonRaidBattleState.Intermission &&
                context.Session.CurrentRaidBattle(actor) is { Kind: RaidKind.Anton } anton)
            {
                // 86顺序STATE→SET_TIMER→REMAIN_TIME；泵写帧时重算剩余值，旧view不能续新阶段。
                context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_SET_TIMER, 13,
                    () => anton.AntonWaitSeconds(view) is { } seconds && seconds > 0
                        ? RaidCombatPackets.AntonWaitTimer(seconds, checked((uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds))) : null,
                    "anton_wait_timer", () => current() && context.RaidDisplay == identity);
                context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_REMAIN_TIME, 5,
                    () => anton.AntonWaitSeconds(view) is { } seconds
                        ? RaidCombatPackets.AntonWaitRemaining(view.AntonPreparing ? (byte)0 : (byte)1, seconds) : null,
                    "anton_wait_remaining", () => current() && context.RaidDisplay == identity);
                if (view.AntonPreparing)
                    context.Sender.Post(0, (ushort)NotiPacketType.PREPARE_START_RAID, [], "anton_phase2_prepare", current);
            }
            if (view.State == AntonRaidBattleState.Intermission &&
                context.Session.CurrentRaidBattle(actor) is { Kind: RaidKind.Luke } luke)
            {
                // 597的key1为已证待命标题；不套用Anton绝对时钟或Luke零秒RESET。
                context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_REMAIN_TIME, 5,
                    () => luke.LukeWaitSeconds(view) is { } seconds ? RaidCombatPackets.AntonWaitRemaining(1, seconds) : null,
                    "luke_wait_remaining", () => current() && context.RaidDisplay == identity);
                if (view.LukePreparing)
                    context.Sender.Post(0, (ushort)NotiPacketType.PREPARE_START_RAID, [], "luke_phase2_prepare", current);
            }
            // 778没有阶段字段；对应587确实写出后才可按该phase解释used，不发送P1假计数。
            if (view.Phase == 1 && view.LukePhaseCoinsUsed is { } used)
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_PHASE_COIN_INFO,
                    RaidCombatPackets.PhaseCoins(used), "raid_phase_coins", () => current() && context.RaidDisplay == identity);
            if (view.State == AntonRaidBattleState.Fighting && view.PhaseClockGeneration > 0 &&
                context.Session.CurrentRaidBattle(actor) is { } battle && (battle.Kind is RaidKind.Luke or RaidKind.Prey || battle.HasAntonRewards))
            {
                if (battle.Kind == RaidKind.Anton)
                    context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_SET_TIMER, 13,
                        () => battle.LukePhaseSeconds(view.Instance, view.Phase, view.PhaseClockGeneration) is { } remaining
                            ? RaidCombatPackets.AntonWaitTimer(remaining, checked((uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + remaining))) : null,
                        "anton_phase_timer", () => current() && context.RaidDisplay == identity);
                context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_REMAIN_TIME, 5,
                    () => battle.LukePhaseSeconds(view.Instance, view.Phase, view.PhaseClockGeneration) is { } seconds
                        ? RaidCombatPackets.PhaseRemaining(seconds) : null, battle.Kind == RaidKind.Anton ? "anton_phase_remaining" : "luke_phase_remaining",
                    () => current() && context.RaidDisplay == identity);
            }
            return true;
        });
    }

    internal static void PostLukeSnapshot(GameConnectionContext context, AntonRaidBattle battle,
        AntonRaidBattleView view, Func<bool> guard, bool restore = false)
    {
        if (battle.Kind != RaidKind.Luke || view.Instance != battle.Instance) return;
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_SET_SYMBOL,
            RaidCombatPackets.Symbols(battle.SymbolSnapshot(view)), "raid_symbol_snapshot", guard);
        // 覆盖本实例全部15图；非当前phase显式Hide，防上阶段/旧团残留Open。
        // 586缺键会创建记录；不用585未知尾字段改变共享symbol7/8。
        foreach (var row in battle.DungeonSnapshot(view))
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_CHANGE_DUNGEON_STATE,
                RaidCombatPackets.DungeonState(row.Id, 0, row.State), "raid_dungeon_snapshot",
                // 显式开团恢复补齐非通关记录；Clear仍去重，避免重播横幅。
                () => guard() && (restore && row.State != GameWorld.Raids.RaidDungeonState.Clear ||
                    DungeonChanged(context, view.Instance, row.Id, row.State)),
                () => RecordAntonDungeon(context, view.Instance, row.Id, row.State));
        PostLukeTimers(context, battle, view, guard);
        PostLukeCards(context, battle, view, guard);
    }

    internal static void PostPreySnapshot(GameConnectionContext context, AntonRaidBattle battle,
        AntonRaidBattleView view, Func<bool> guard)
    {
        if (battle.Kind != RaidKind.Prey || view.Instance != battle.Instance) return;
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_SET_SYMBOL,
            RaidCombatPackets.Symbols(battle.SymbolSnapshot(view)), "prey_symbol_snapshot", guard);
        foreach (var row in battle.DungeonSnapshot(view))
        {
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_CHANGE_DUNGEON_STATE,
                RaidCombatPackets.DungeonState(row.Id, 0, row.State), "prey_dungeon_snapshot",
                () => guard() && DungeonChanged(context, view.Instance, row.Id, row.State),
                row.State != GameWorld.Raids.RaidDungeonState.Clear ? () => RecordAntonDungeon(context, view.Instance, row.Id, row.State) : null);
            if (row.State == GameWorld.Raids.RaidDungeonState.Clear)
                context.Sender.Post(0, (ushort)NotiPacketType.PREY_RAID, RaidCombatPackets.PreyStatus(5, row.Id),
                    "prey_dungeon_cleared", () => guard() && DungeonChanged(context, view.Instance, row.Id, row.State),
                    () => RecordAntonDungeon(context, view.Instance, row.Id, row.State));
        }
        foreach (var timer in view.Timers.Keys.Where(timer => timer.Kind is 1 or 2))
            context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_SET_TIMER, 13,
                () => battle.PreyTimerSeconds(view, timer) is { } seconds
                    // 01DD06B0频道50忽略中列，以now+末列保存截止点；补发只写当前剩余值。
                    ? RaidCombatPackets.LukePositiveTimer(checked((sbyte)timer.Kind), timer.DungeonId, 0, seconds) : null,
                "prey_rule_timer", guard);
    }

}
