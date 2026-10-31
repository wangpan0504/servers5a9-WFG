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
    public Action<AntonRaidBattleView, RaidActor>? RaidBattleNoticeArrived { get; set; }

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

    internal RaidKind? RaidCombatKind() => owner.Execute(() => raidCombat is { Actor.IsCurrent: true } raid ? (RaidKind?)raid.Battle.Kind : null);

    internal bool RaidPhaseProgress => owner.Execute(() => raidCombat is { Battle.PhaseProgress: true });

    public AntonRaidBattleView? RaidCombatView() => owner.Execute(() =>
        raidCombat is { } raid && raid.Actor.IsCurrent ? raid.Battle.View : null);

    // 团本单图没有普通46结算；只允许当前安徒恩已提交清图的菜单，不扩大普通翻牌资格。
    // 安徒恩战况表在图内提交C2S72 `01 02`（回到城镇）：只允许当前Anton正式单图仍在Dungeon阶段且无待提交死亡/结算/地面操作，
    // 复用42同一分队退场事务；不扣DGN HP、不产生ClearFact，也不代表`01 01`（进入地下城）已实现。
    internal bool AntonDungeonBoardReturn() => owner.Execute(() =>
        Ready && Phase == SessionPhase.Dungeon && raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Anton } &&
        formalDungeon is { Entry.RaidEntry: not null } dungeon &&
        !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement && !dungeon.Ground.HasPendingOperation &&
        !HasPendingFormalCard && pendingQuestCompletion is null && !HasPendingQuestScene);

    internal FormalDungeonReceipt? AntonDungeonResultFocus() => owner.Execute(() =>
        Ready && Phase == SessionPhase.Settlement && raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Anton } &&
        formalDungeon is { Entry.RaidEntry: not null, Run.ClearFact: not null } dungeon &&
        !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement && !dungeon.Ground.HasPendingOperation &&
        !HasPendingFormalCard && pendingQuestCompletion is null && !HasPendingQuestScene
            ? FormalReceipt() : null);

    internal bool CanRetryAntonResultDungeon() => owner.Execute(() =>
        AntonDungeonResultFocus() is { } receipt && raidCombat is { } raid &&
        raid.Battle.AllowsAntonEntry(checked((int)receipt.Entry.DungeonId)));

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
        if (!raid.Battle.HasAntonRewards) return policy.Result;
        if (raid.Battle.AntonResult(view.Phase) is not { } result ||
            raid.Battle.AntonEligibility(raid.Actor.Character, view.Phase) is not { } eligibility) return null;
        // 86合格成员option0，无资格option1；95按是否零消费，不以显示资格替代存储屏障。
        return result with { RawFlag = eligibility.Eligible ? (byte)0 : (byte)1 };
    });

    public bool CanPublishRaidBattle(RaidActor actor, AntonRaidBattleView view) => owner.Execute(() =>
        raidCombat is { } raid && raid.Actor == actor && actor.IsCurrent && raid.Battle.IsCurrent(view));

    // 117协调已提交死亡，不能由695、客户端symbol或复制的ClearFact创建阶段胜利。
    public AntonRaidBattleChange? CompleteRaidCombatDungeon(FormalDungeonReceipt receipt)
    {
        var completedChange = owner.Execute<AntonRaidBattleChange?>(() =>
        {
            if (raidCombat is not { } raid || raid.Battle.Kind == RaidKind.Luke || !raid.Actor.IsCurrent || formalDungeon is not { } dungeon ||
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
        var battle = owner.Execute(() => raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Luke } raid ? raid.Battle : null);
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
        ItemCatalog items, GoldLimitCatalog gold, EpicPieceCatalog pieces, DateTimeOffset now)
    {
        CommitPendingAntonPhase();
        var capture = owner.Execute(() =>
        {
            if (!Ready || Selected is not { } character || raidCombat is not { } raid || !raid.Actor.IsCurrent ||
                !Enum.IsDefined(raid.Battle.Kind)) return null;
            var view = raid.Battle.View;
            if (view.ClockFaulted) return null;
            int phase = view.Phase;
            if (view.State != AntonRaidBattleState.Reward)
            {
                var old = raid.Receipts.Where(r => r.Key.Kind == kind).Select(r => r.Value).MaxBy(r => r.Prepared.Phase);
                return old is not null && old.Prepared.Card == card ? new RaidRewardCapture(raid, character, old.Prepared, old.Offer, old) : null;
            }
            var eligibility = raid.Battle.Kind == RaidKind.Luke ? raid.Battle.LukeEligibility(character.Id, phase) :
                raid.Battle.HasAntonRewards ? raid.Battle.AntonEligibility(character.Id, phase) : null;
            if ((raid.Battle.Kind == RaidKind.Luke || raid.Battle.HasAntonRewards) && eligibility is not { Eligible: true }) return null;
            var policy = raid.Battle.RewardPolicy(raid.Actor, phase);
            var offer = policy?.Offers.SingleOrDefault(o => o.Kind == kind && o.Card == card);
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
            utcNow: now, allowOverflowMail: true);
        if (!granted.Accepted || granted.Plan is not { } plan) return null;
        var inventory = new InventoryPlan(plan.Before, plan.After, "raid_reward", new(capture.Character), plan.OverflowItems);
        return owner.Execute(() =>
        {
            if (!ReferenceEquals(Selected, capture.Character) || !ReferenceEquals(raidCombat, capture.Binding) ||
                !capture.Binding.Actor.IsCurrent || capture.Binding.Battle.View is not { State: AntonRaidBattleState.Reward } view ||
                view.Phase != capture.Prepared.Phase) return null;
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
            view.Phase != receipt.Prepared.Phase || raid.Battle.LukeEligibility(receipt.Prepared.Character, view.Phase) is not { Eligible: true } eligibility ||
            eligibility.Offers.SingleOrDefault(offer => offer.Kind == receipt.Prepared.Kind && offer.Card == receipt.Prepared.Card) is not { } offer ||
            !SameLukeResultOffer(offer, receipt.Offer)) return null;
        return (raid.Actor, view);
    });

    static bool SameLukeResultOffer(RaidRewardOffer left, RaidRewardOffer right) =>
        left.Kind == right.Kind && left.Card == right.Card && left.LukeResult == right.LukeResult && left.Grants.SequenceEqual(right.Grants);

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
