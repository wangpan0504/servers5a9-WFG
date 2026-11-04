using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed class RaidCombatBinding(RaidActor actor, AntonRaidBattle battle)
    {
        public RaidActor Actor { get; } = actor;
        public AntonRaidBattle Battle { get; } = battle;
        public Dictionary<(int Phase, byte Kind), RaidRewardReceipt> Receipts { get; } = [];
        public Dictionary<(int Phase, byte Kind), PreparedRaidReward> Pending { get; } = [];
        public int? RewardWeek { get; set; }
    }
    RaidCombatBinding? raidCombat;
    internal bool CanPrepareLukeNextPhase(RaidActor actor, AntonRaidBattle battle) => owner.Execute(() =>
        CanChangeTownParty && MatchesRaidCombatActor(actor) && raidCombat is { } raid && raid.Actor == actor &&
        ReferenceEquals(raid.Battle, battle) && formalDungeon is null && PartyDungeon is null &&
        battle.IsLukeWaitingRoom(actor));
    internal Action<RaidRewardReceipt>? AntonAutoRewardArrived { get; set; }
    internal Func<AntonRaidBattle, int, bool>? AntonAutoRewardRequested { get; set; }

    internal bool AutoClaimAntonReward(AntonRaidBattle battle, int phase, IRaidRewardStore store,
        ItemCatalog items, GoldLimitCatalog gold, EpicPieceCatalog pieces, DateTimeOffset now)
    {
        // 旧owner/离线成员保留明确未补发边界；不能借后来登录的新会话提交。
        bool Current() => owner.IsCurrent && owner.Execute(() => Ready && raidCombat is { Actor.IsCurrent: true } binding &&
            ReferenceEquals(binding.Battle, battle) && binding.Battle.View.Phase == phase &&
            binding.Battle.View.State == AntonRaidBattleState.Reward);
        try
        {
            if (!Current()) return true;
            var kinds = owner.Execute(() => raidCombat is { } raid && ReferenceEquals(raid.Battle, battle) && Selected is { } character
                ? battle.AntonEligibility(character.Id, phase)?.Offers.Select(offer => offer.Kind)
                    .OrderBy(kind => kind == 1 ? 0 : kind == 2 ? 1 : kind == 3 ? 2 : 3).ToArray() ?? [] : []);
            foreach (byte kind in kinds)
            {
                if (!Current()) return true;
                var receipt = ClaimRaidReward(kind, 0, store, items, gold, pieces, now, automaticLuke: kind == 3);
                if (receipt is null) return !Current();
                // 已提交资产先排入原连接FIFO；自动领取不伪造客户端请求的ACK。
                AntonAutoRewardArrived?.Invoke(receipt);
                ConfirmRaidRewardPublished(receipt);
            }
            return true;
        }
        catch (InvalidOperationException) when (!owner.IsCurrent)
        {
            // 检查后发生接管属于原成员失效，不能因此故障整个Battle或借新owner发奖。
            return true;
        }
    }
    public Action<AntonRaidBattleView, RaidActor>? RaidBattleNoticeArrived { get; set; }
    internal Action<AntonRaidBattle.LukeRunCleared, RaidActor>? RaidRunClearedArrived { get; set; }

    internal bool CanPublishLukeRunCleared(RaidActor actor, AntonRaidBattle.LukeRunCleared cleared) => owner.Execute(() =>
        Ready && actor.IsCurrent && raidCombat is { } raid && raid.Actor == actor &&
        ReferenceEquals(raid.Battle, cleared.Battle) && raid.Battle.Kind == RaidKind.Luke);
    internal Func<RaidRewardReceipt, Task>? AutomaticRaidRewardArrived { get; set; }

    // START在正式开战提交后安装；MAP在安装正式副本后调用BindRaidCombatDungeon。无协议探测状态。
    public bool AttachRaidCombat(RaidActor actor, AntonRaidBattle battle, ImmutableArray<RaidPhaseRewardPolicy> policies) => owner.Execute(() =>
    {
        if (!Ready || raidCombat is not null || !ReferenceEquals(actor.Session, this) || !ReferenceEquals(actor.Owner, owner) || !actor.IsCurrent ||
            !battle.Join(actor, policies)) return false;
        raidCombat = new(actor, battle); return true;
    });

    // SQL前分配绑定并登记到尚未发布的新Battle；失败时整个对象丢弃，无Session状态写入。
    bool MatchesRaidCombatActor(RaidActor actor) => ReferenceEquals(actor.Owner, owner) && ReferenceEquals(actor.Session, this) && actor.IsCurrent;

    internal RaidCombatStartBinding? PrepareRaidCombatStart(RaidActor actor, AntonRaidBattle battle) =>
        RaidCombatStartBinding.Prepare(this, actor, battle);

    internal sealed class RaidCombatStartBinding
    {
        readonly GameSession session;
        readonly RaidCombatBinding binding;
        private RaidCombatStartBinding(GameSession session, RaidCombatBinding binding)
        { this.session = session; this.binding = binding; }
        internal static RaidCombatStartBinding? Prepare(GameSession session, RaidActor actor, AntonRaidBattle battle)
        {
            if (!session.Ready || session.raidCombat is not null || !ReferenceEquals(actor.Session, session) ||
                !session.MatchesRaidCombatActor(actor) || !battle.JoinStart(actor)) return null;
            return new(session, new(actor, battle));
        }
        public void Install() => binding.Actor.Owner.Execute(() =>
        {
            if (!session.Ready || !session.MatchesRaidCombatActor(binding.Actor) || session.raidCombat is not null)
                throw new InvalidOperationException("攻坚开战绑定不再属于当前会话");
            session.raidCombat = binding;
            return true;
        });
    }

    internal RaidRewardReceipt? ClaimAutomaticAntonSquadReward(IRaidRewardStore store, ItemCatalog items,
        GoldLimitCatalog gold, EpicPieceCatalog pieces, DateTimeOffset now)
    {
        bool available = owner.Execute(() => raidCombat is { Actor.IsCurrent: true, Battle.HasAntonRewards: true } raid &&
            raid.Battle.View is { State: AntonRaidBattleState.Reward, AntonCardsReady: true } view &&
            raid.Battle.RewardPolicy(raid.Actor, view.Phase) is { } policy && policy.Offers.Any(offer => offer.Kind == 3) &&
            policy.Offers.Where(offer => offer.Kind is 1 or 2).All(offer => raid.Receipts.ContainsKey((view.Phase, offer.Kind))));
        return available ? ClaimRaidReward(3, 0, store, items, gold, pieces, now, automaticLuke: true) : null;
    }

    // 新版660 kind0只是四张团队牌的原选位/空牌背请求；不签发奖励计划或资产回执。
    internal (RaidActor Actor, AntonRaidBattleView View, byte Card)? SelectAntonEmptyCardBack(byte requested) =>
        owner.Execute<(RaidActor, AntonRaidBattleView, byte)?>(() =>
        {
            if (!Ready || AntonRejoinPresentationPending || Selected is not { } character || raidCombat is not { Actor.IsCurrent: true, Battle.HasAntonRewards: true } raid ||
                raid.Battle.View is not { } view ||
                raid.Battle.RewardPolicy(raid.Actor, view.Phase)?.Source != AntonResourceRewards.Source) return null;
            // 团队及小队已提交后可能已经进入休息/完成；自然客户端紧随其后的0仍只确认原选位。
            if (raid.Receipts.TryGetValue((view.Phase, 1), out var committed))
                return committed.Prepared.Card == requested || raid.Battle.MatchesAntonCardReplay(committed.Prepared, requested)
                    ? (raid.Actor, view, committed.Prepared.Card) : null;
            if (view.State != AntonRaidBattleState.Reward) return null;
            byte squad = StartedRaid?.Participants.SingleOrDefault(row => row.Character == character.Id).Squad ?? 0;
            var offer = raid.Battle.SelectAntonRewardCard(raid.Actor, view.Phase, 1, requested, squad);
            return offer is null ? null : (raid.Actor, view, offer.Card);
        });

    internal bool DetachRaidCombatStart(RaidActor actor, Guid instance) => owner.Execute(() =>
    {
        if (raidCombat is not { } current || current.Actor != actor ||
            current.Battle.Instance != instance || !MatchesRaidCombatActor(actor)) return false;
        current.Battle.DetachStart(actor);
        raidCombat = null;
        return true;
    });

    public bool BindRaidCombatDungeon(Guid instance, int phase) => owner.Execute(() =>
        raidCombat is { } raid && raid.Actor.IsCurrent && formalDungeon is { Run.ClearFact: null } dungeon &&
        raid.Battle.Instance == instance && raid.Battle.View.Phase == phase &&
        raid.Battle.BindDungeon(dungeon.Run.Snapshot.Identity, dungeon.Run.DungeonId));

    internal AntonRaidBattle? CurrentRaidBattle(RaidActor actor) => owner.Execute(() =>
        raidCombat is { } raid && raid.Actor == actor && actor.IsCurrent ? raid.Battle : null);

    // 跨成员名册只读原绑定及Battle快照，不在离团者owner内取得另一账号锁。
    // 仅用于显示；任何写入仍须沿原owner及正式业务入口。
    internal IReadOnlyDictionary<ushort, byte>? RaidRosterClearSnapshot(RaidActor actor, Guid instance)
    {
        var binding = Volatile.Read(ref raidCombat);
        if (binding is null || binding.Actor != actor || !actor.IsCurrent || binding.Battle.Instance != instance) return null;
        var counts = binding.Battle.RosterClears();
        return actor.IsCurrent && ReferenceEquals(binding, Volatile.Read(ref raidCombat)) ? counts : null;
    }

    internal (AntonRaidBattle Battle, AntonRaidBattleView View)? RaidRosterBattleSnapshot(RaidActor actor, Guid instance)
    {
        var binding = Volatile.Read(ref raidCombat);
        if (binding is null || binding.Actor != actor || !actor.IsCurrent || binding.Battle.Instance != instance) return null;
        var view = binding.Battle.View;
        return actor.IsCurrent && ReferenceEquals(binding, Volatile.Read(ref raidCombat)) ? (binding.Battle, view) : null;
    }

    internal (FormalDungeonReceipt Scene, byte Squad, int? MonitorCount)? CaptureAntonHp() =>
        owner.Execute<(FormalDungeonReceipt, byte, int?)?>(() =>
            Ready && Phase == SessionPhase.Dungeon && raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Anton or RaidKind.Luke or RaidKind.Prey } raid &&
            raid.Battle.View is { State: AntonRaidBattleState.Fighting, PhaseExpired: false } view &&
            formalDungeon is { Entry.RaidEntry: { Squad: >= 1 and <= 20 } entry, Run.ClearFact: null } dungeon && entry.Kind == raid.Battle.Kind &&
            entry.InstanceId == raid.Battle.Instance && entry.Phase == view.Phase && CanMoveWithParty()
                ? (FormalReceipt(), entry.Squad, raid.Battle.LukeMonitorCount(raid.Actor, dungeon.Run.Snapshot.Identity)) : null);

    internal AntonRaidBattleChange? ReportRaidStatusHp(FormalDungeonReceipt scene, Dungeons.AntonHpDisplay display) =>
        owner.Execute(() => CanPublishFormalDungeon(scene) && raidCombat is { Actor.IsCurrent: true } raid
            ? raid.Battle.ReportStatusHp(raid.Actor, scene.Snapshot.Identity, display) : null);

    internal (RaidActor Actor, AntonRaidBattle Battle, AntonRaidBattleView View)? RaidStatusSnapshot() =>
        owner.Execute<(RaidActor, AntonRaidBattle, AntonRaidBattleView)?>(() =>
            Ready && raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Anton or RaidKind.Luke or RaidKind.Prey } raid
                ? (raid.Actor, raid.Battle, raid.Battle.View) : null);

    internal AntonRaidBattleChange? RecordLukeNamedDeath(FormalDungeonDeathResult result) => owner.Execute(() =>
        result is { Outcome: DungeonRunOutcome.Accepted, Receipt: { } scene, ReportedTarget: { } target } &&
        CanPublishFormalDungeon(scene) && !scene.Snapshot.LivingMonsters.Contains(result.MonsterKey) &&
        formalDungeon is { } dungeon && dungeon.Run.CommittedMonitorDeath(result.MonsterKey, out int template, out _) && template == target.MonsterId &&
        raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Luke } raid
            ? raid.Battle.RecordLukeNamedDeath(raid.Actor, scene.Snapshot.Identity, dungeon.Run, result.MonsterKey) : null);

    internal RaidKind? RaidCombatKind() => owner.Execute(() => raidCombat is { Actor.IsCurrent: true } raid ? (RaidKind?)raid.Battle.Kind : null);

    internal bool RaidPhaseProgress => owner.Execute(() => raidCombat is { Battle.PhaseProgress: true });

    public AntonRaidBattleView? RaidCombatView() => owner.Execute(() =>
        raidCombat is { } raid && raid.Actor.IsCurrent ? raid.Battle.View : null);

    // 团本单图没有普通46结算；卢克与安徒恩均从原Run的已提交清图事实进入返回菜单。
    // 战况表在图内提交C2S72 `01 02`（回到城镇）：只允许当前正式团本单图仍在Dungeon阶段且无待提交死亡/结算/地面操作，
    // 复用42同一分队退场事务；不扣DGN HP、不产生ClearFact，也不代表`01 01`（进入地下城）已实现。
    internal bool CanReturnFromRaidDungeonBoard() => owner.Execute(() =>
        Ready && Phase == SessionPhase.Dungeon && raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Anton or RaidKind.Luke or RaidKind.Prey or RaidKind.Fiendwar } raid &&
        formalDungeon is { Entry.RaidEntry: not null } dungeon &&
        dungeon.Entry.RaidEntry.Kind == raid.Battle.Kind && dungeon.Entry.RaidEntry.InstanceId == raid.Battle.Instance &&
        !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement && !dungeon.Ground.HasPendingOperation &&
        !HasPendingFormalCard && pendingQuestCompletion is null && !HasPendingQuestScene);

    internal FormalDungeonReceipt? RaidDungeonResultFocus() => owner.Execute(() =>
        Ready && Phase == SessionPhase.Settlement && raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Anton or RaidKind.Luke or RaidKind.Prey or RaidKind.Fiendwar } raid &&
        formalDungeon is { Entry.RaidEntry: not null, Run.ClearFact: not null } dungeon &&
        dungeon.Entry.RaidEntry.Kind == raid.Battle.Kind && dungeon.Entry.RaidEntry.InstanceId == raid.Battle.Instance &&
        (raid.Battle.Kind is not (RaidKind.Luke or RaidKind.Fiendwar) ||
            raid.Battle.HasDungeonContribution(raid.Actor, dungeon.Run.Snapshot.Identity.InstanceId)) &&
        !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement && !dungeon.Ground.HasPendingOperation &&
        !HasPendingFormalCard && pendingQuestCompletion is null && !HasPendingQuestScene
            ? FormalReceipt() : null);

    internal bool CanRetryRaidResultDungeon() => owner.Execute(() =>
    {
        if (RaidDungeonResultFocus() is not { Entry.RaidEntry: { } identity } receipt || raidCombat is not { } raid ||
            identity.Phase != raid.Battle.View.Phase) return false;
        int dungeon = checked((int)receipt.Entry.DungeonId);
        return raid.Battle.Kind switch
        {
            RaidKind.Luke => raid.Battle.LukePhaseOpen(identity.Phase) &&
                raid.Battle.View.Dungeons.TryGetValue(dungeon, out var state) && state == GameWorld.Raids.RaidDungeonState.Open &&
                raid.Battle.AllowsLukeMemberRoute(raid.Actor, identity.Phase, dungeon),
            RaidKind.Prey => raid.Battle.AllowsPreyEntry(dungeon),
            RaidKind.Anton => raid.Battle.AllowsAntonEntry(dungeon),
            _ => false
        };
    });

    // 选图只读取原实例聚合，不另存一份初始战况；离团、换角与接管后不可重发旧列表。
    internal (RaidActor Actor, AntonRaidBattle Battle)? AntonRaidMap() => owner.Execute<(RaidActor, AntonRaidBattle)?>(() =>
        Ready && ChannelEntry?.Type == 23 && Selected?.Position is { Town: 19, Area: 2 } &&
        raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Anton } raid && StartedRaid?.InstanceId == raid.Battle.Instance
            ? (raid.Actor, raid.Battle) : null);

    internal (RaidActor Actor, AntonRaidBattle Battle, AntonRaidBattleView View)? AntonRaidTimerSnapshot() =>
        owner.Execute<(RaidActor, AntonRaidBattle, AntonRaidBattleView)?>(() =>
            Ready && raidCombat is { Actor.IsCurrent: true, Battle.HasAntonRewards: true } raid
                ? (raid.Actor, raid.Battle, raid.Battle.View) : null);

    public RaidResultDisplay? RaidCombatResult(AntonRaidBattleView view) => owner.Execute(() =>
    {
        if (raidCombat is not { } raid || !raid.Actor.IsCurrent || !raid.Battle.IsCurrent(view) ||
            raid.Battle.RewardPolicy(raid.Actor, view.Phase) is not { PublishResult: true } policy) return null;
        if (!raid.Battle.HasAntonRewards) return raid.Battle.LukeResultSeconds(view.Phase) is { } seconds
            ? policy.Result with { Seconds = seconds } : policy.Result;
        if (raid.Battle.AntonResult(view.Phase) is not { } result ||
            raid.Battle.AntonEligibility(raid.Actor.Character, view.Phase) is not { } eligibility) return null;
        // 95的F为零抑制阶段任务；只有同事务已承诺任务处理的合格回执可放行原生事件。
        bool sameTasks = eligibility is { Eligible: true, AntonQuestProgress: { } progress } && Selected is { } character &&
            SameAntonQuestIdentities(character.ActiveMainQuests, progress.After) &&
            SameAntonQuestIdentities(character.ActiveMainQuests, progress.Bindings) &&
            CanTriggerNativeAntonQuests(character.ActiveMainQuests, questCatalog);
        return result with { RawFlag = sameTasks ? (byte)1 : (byte)0 };
    });

    internal (RaidActor Actor, AntonRaidBattle Battle, AntonRaidBattleView View)? RaidMovieSnapshot() =>
        owner.Execute<(RaidActor, AntonRaidBattle, AntonRaidBattleView)?>(() =>
        {
            if (!Ready || AntonRejoinPresentationPending || raidCombat is not { Actor.IsCurrent: true } raid ||
                raid.Battle.View is not { State: AntonRaidBattleState.Reward, PhaseExpired: false, ClockFaulted: false } view ||
                raid.Battle.RewardPolicy(raid.Actor, view.Phase) is not { PublishResult: true } ||
                !(raid.Battle.HasAntonRewards ? raid.Battle.CanConfirmAntonMovie(raid.Actor, view) :
                    raid.Battle.Kind == RaidKind.Fiendwar ? raid.Battle.CanConfirmFiendwarMovie(raid.Actor, view) :
                    raid.Battle.CanConfirmLukeMovie(raid.Actor, view))) return null;
            return (raid.Actor, raid.Battle, view);
        });

    public bool CanPublishRaidBattle(RaidActor actor, AntonRaidBattleView view) => owner.Execute(() =>
        raidCombat is { } raid && raid.Actor == actor && actor.IsCurrent && raid.Battle.IsCurrent(view));

    // 117协调已提交死亡，不能由695、客户端symbol或复制的ClearFact创建阶段胜利。
    public AntonRaidBattleChange? CompleteRaidCombatDungeon(FormalDungeonReceipt receipt)
    {
        var completedChange = owner.Execute<AntonRaidBattleChange?>(() =>
        {
            if (raidCombat is not { } raid || raid.Battle.Kind is RaidKind.Luke or RaidKind.Prey or RaidKind.Fiendwar || !raid.Actor.IsCurrent || formalDungeon is not { } dungeon ||
                receipt.Generation != owner.Generation || receipt.SelectionEpoch != SelectionEpoch ||
                receipt.Snapshot.Identity != dungeon.Run.Snapshot.Identity || dungeon.Run.ClearFact is not { } fact ||
                fact.Identity != receipt.Snapshot.Identity || !dungeon.Entry.IsTerminalMap(dungeon.Run.Snapshot.CurrentMap)) return null;
            int? week = DungeonIncountPolicy.Period((timeProvider ?? TimeProvider.System).GetUtcNow()).Week;
            var change = raid.Battle.CompleteDungeon(fact, week) ?? raid.Battle.CompleteAntonReservedDungeon(fact, week);
            // 忙锁重试117时清图事实已封存；继续提交同一候选，不让重放只回115而遗失Reward。
            if (change is null && raid.Battle.PendingAntonSettlement() is not null)
            { var pending = raid.Battle.View; change = new(pending, pending); }
            if (change?.After.State == AntonRaidBattleState.Reward)
                raid.RewardWeek ??= DungeonIncountPolicy.Period((timeProvider ?? TimeProvider.System).GetUtcNow()).Week;
            return change;
        });
        if (completedChange is null) return null;
        CommitPendingAntonPhase();
        return new(completedChange.Before, RaidCombatView() ?? completedChange.After);
    }

    public AntonRaidBattleChange? CompleteCurrentRaidCombatDungeon()
    {
        var receipt = owner.Execute(() => formalDungeon?.Run.ClearFact is not null ? FormalReceipt() : null);
        return receipt is null ? null : CompleteRaidCombatDungeon(receipt);
    }

    // 纯生命周期writer在自身owner短读取之后执行，不在本分队owner/Battle/Party领域锁内扩锁或等待SQL。
    internal bool CommitPendingLukePhase(ILukeRaidLifecycleStore store)
    {
        var battle = owner.Execute(() => raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Luke or RaidKind.Prey or RaidKind.Fiendwar } raid ? raid.Battle : null);
        if (battle?.PendingLukeSettlement() is not { } candidate) return false;
        var receipt = store.CommitLukePhase(candidate);
        _ = battle.AcceptLukeSettlement(candidate, receipt);
        return true;
    }

    internal void PublishRaidCombat(AntonRaidBattleChange change)
    {
        var battle = owner.Execute(() => raidCombat is { Actor.IsCurrent: true } raid && raid.Battle.Instance == change.After.Instance ? raid.Battle : null);
        battle?.Publish(change.After);
    }

    public void PublishRaidCombat()
    {
        CommitPendingAntonPhase();
        var battle = owner.Execute(() => raidCombat?.Battle);
        battle?.Publish(battle.View);
    }

    public RaidRewardReceipt? ClaimRaidReward(byte kind, byte card, IRaidRewardStore store,
        ItemCatalog items, GoldLimitCatalog gold, EpicPieceCatalog pieces, DateTimeOffset now, bool automaticLuke = false)
    {
        CommitPendingAntonPhase();
        var capture = owner.Execute(() =>
        {
            if (!Ready || AntonRejoinPresentationPending || Selected is not { } character || raidCombat is not { } raid || !raid.Actor.IsCurrent ||
                !Enum.IsDefined(raid.Battle.Kind)) return null;
            // kind3没有原生手动请求；已经提交的自动回执也不能被外部660借道重新播放终结动画。
            if (raid.Battle.HasAntonRewards && kind == 3 && !automaticLuke) return null;
            var view = raid.Battle.View;
            if (view.ClockFaulted) return null;
            int phase = view.Phase;
            if (view.State != AntonRaidBattleState.Reward)
            {
                var old = raid.Receipts.Where(r => r.Key.Kind == kind).Select(r => r.Value).MaxBy(r => r.Prepared.Phase);
                return old is not null && (old.Prepared.Card == card || raid.Battle.MatchesAntonCardReplay(old.Prepared, card))
                    ? new RaidRewardCapture(raid, character, old.Prepared, old.Offer, old) : null;
            }
            // 已提交回执仍允许重放；动画未就绪时结果投影另外拒绝提前打开UI。
            if (raid.Receipts.TryGetValue((phase, kind), out var settled))
                return settled.Prepared.Card == card || raid.Battle.MatchesAntonCardReplay(settled.Prepared, card)
                    ? new RaidRewardCapture(raid, character, settled.Prepared, settled.Offer, settled) : null;
            if (!raid.Battle.CanClaimRaidReward(raid.Actor, phase, automaticLuke)) return null;
            var eligibility = raid.Battle.Kind is RaidKind.Luke or RaidKind.Prey or RaidKind.Fiendwar ? raid.Battle.LukeEligibility(character.Id, phase) :
                raid.Battle.HasAntonRewards ? raid.Battle.AntonEligibility(character.Id, phase) : null;
            if ((raid.Battle.Kind is RaidKind.Luke or RaidKind.Prey or RaidKind.Fiendwar || raid.Battle.HasAntonRewards) && eligibility is not { Eligible: true }) return null;
            var policy = raid.Battle.RewardPolicy(raid.Actor, phase);
            var offer = raid.Battle.SelectableRewardCards
                ? raid.Battle.SelectAntonRewardCard(raid.Actor, phase, kind, card,
                    StartedRaid?.Participants.SingleOrDefault(row => row.Character == character.Id).Squad ?? 0, automaticLuke)
                : policy?.Offers.SingleOrDefault(o => o.Kind == kind && o.Card == card);
            if (offer is not null) card = offer.Card;
            if (offer is null) return null;
            if (raid.Receipts.TryGetValue((phase, kind), out var replay))
                return replay.Prepared.Card == card ? new RaidRewardCapture(raid, character, replay.Prepared, replay.Offer, replay) : null;
            if (!raid.Pending.TryGetValue((phase, kind), out var pending))
            {
                pending = new(raid.Battle.Instance, phase, kind, card, character.Id, policy!.Source, offer.Grants,
                    phase + 1 == raid.Battle.PhaseCount, eligibility?.Week ?? raid.Battle.RewardWeek ?? raid.RewardWeek ?? DungeonIncountPolicy.Period(now).Week)
                    { RaidType = raid.Battle.Kind };
                raid.RewardWeek = pending.Week;
                raid.Pending.Add((phase, kind), pending);
            }
            return pending.Card == card ? new RaidRewardCapture(raid, character, pending, offer, null) : null;
        });
        if (capture is null) return null;
        if (capture.Replay is { } duplicate) return duplicate with { Commit = duplicate.Commit with { Duplicate = true }, Inventory = new(true, 0) };
        // 资源解析和批量放置不占账号锁；结果冻结后再复验同一角色及阶段。
        var granted = InventoryRewardGrantService.TryGrantBatch(capture.Character, capture.Prepared.Grants, items, gold, pieces,
            utcNow: now, allowOverflowMail: true, raidReward: true);
        if (!granted.Accepted || granted.Plan is not { } plan) return null;
        var inventory = new InventoryPlan(plan.Before, plan.After, "raid_reward", new(capture.Character), plan.OverflowItems)
            { AccountMaterialRaidWeek = plan.AccountMaterialRaidWeek, AccountMaterialRaidDefinitions = plan.AccountMaterialRaidDefinitions };
        return owner.Execute(() =>
        {
            if (AntonRejoinPresentationPending || !ReferenceEquals(Selected, capture.Character) || !ReferenceEquals(raidCombat, capture.Binding) ||
                !capture.Binding.Actor.IsCurrent || capture.Binding.Battle.View is not { State: AntonRaidBattleState.Reward } view ||
                view.Phase != capture.Prepared.Phase ||
                !capture.Binding.Battle.CanClaimRaidReward(capture.Binding.Actor, view.Phase, automaticLuke)) return null;
            var commit = store.CommitRaidReward(owner, capture.Character, capture.Prepared, inventory, now);
            if (commit is null) return null;
            Selected = commit.Character;
            var result = granted with { Plan = new(capture.Character.Inventory, commit.Character.Inventory, "raid_reward"),
                Receipt = new(commit.Character, owner.Generation, SelectionEpoch)
                { Phase = Phase, DungeonIdentity = formalDungeon?.Run.Snapshot.Identity, PreviousQuests = capture.Character.ActiveMainQuests,
                    PreviousSkillSwitchInventory = capture.Character.SkillSwitchInventory } };
            var receipt = new RaidRewardReceipt(capture.Prepared, capture.Offer, commit, result, owner.Generation, SelectionEpoch);
            capture.Binding.Receipts[(capture.Prepared.Phase, kind)] = receipt;
            capture.Binding.Pending.Remove((capture.Prepared.Phase, kind));
            capture.Binding.Battle.RecordLukeRewardReceipt(capture.Binding.Actor, receipt);
            return receipt;
        });
    }

    sealed record RaidRewardCapture(RaidCombatBinding Binding, DfoServer.Game.Characters.Character Character,
        PreparedRaidReward Prepared, RaidRewardOffer Offer, RaidRewardReceipt? Replay);

    public bool CanPublishRaidReward(RaidRewardReceipt receipt) => owner.Execute(() =>
        Ready && receipt.Generation == owner.Generation && receipt.Epoch == SelectionEpoch && raidCombat is { } raid &&
        raid.Actor.IsCurrent && receipt.Prepared.Instance == raid.Battle.Instance && receipt.Prepared.RaidType == raid.Battle.Kind &&
        ReferenceEquals(raid.Receipts.GetValueOrDefault((receipt.Prepared.Phase, receipt.Prepared.Kind))?.Prepared, receipt.Prepared));

    // 原receipt同阶段只读身份，不要求原revision仍是最新；阶段推进后不把旧600装入新窗口。
    internal (RaidActor Actor, AntonRaidBattleView View)? LukeResultView(RaidRewardReceipt receipt) => owner.Execute<(RaidActor, AntonRaidBattleView)?>(() =>
    {
        if (!CanPublishRaidReward(receipt) || raidCombat is not { Battle.Kind: RaidKind.Luke } raid ||
            raid.Battle.View is not { State: AntonRaidBattleState.Reward, ClockFaulted: false, PhaseExpired: false } view ||
            view.Phase != receipt.Prepared.Phase || !raid.Battle.LukeMovieCardsReady(view) || raid.Battle.LukeEligibility(receipt.Prepared.Character, view.Phase) is not { Eligible: true } eligibility ||
            eligibility.Offers.SingleOrDefault(offer => offer.Kind == receipt.Prepared.Kind &&
                offer.Card == (raid.Battle.SelectableRewardCards ? 0 : receipt.Prepared.Card)) is not { } offer ||
            !SameLukeResultOffer(offer with { Card = receipt.Prepared.Card }, receipt.Offer)) return null;
        return (raid.Actor, view);
    });

    static bool SameLukeResultOffer(RaidRewardOffer left, RaidRewardOffer right) =>
        left.Kind == right.Kind && left.Card == right.Card && left.LukeResult == right.LukeResult &&
        left.LukeAdditionalResults.SequenceEqual(right.LukeAdditionalResults) && left.Grants.SequenceEqual(right.Grants);

    public void ConfirmRaidRewardPublished(RaidRewardReceipt receipt)
    {
        var battle = owner.Execute(() =>
        {
            if (!CanPublishRaidReward(receipt) || raidCombat is not { } raid) return null;
            if (raid.Battle.ConfirmReward(raid.Actor, receipt.Prepared))
            { var view = raid.Battle.View; raid.Battle.AdvanceAfterRewards(view.Instance, view.Revision); }
            return raid.Battle;
        });
        battle?.Publish(battle.View);
    }
}
