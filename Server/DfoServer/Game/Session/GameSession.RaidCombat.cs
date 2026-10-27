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
        if (raidCombat is not { Battle.Kind: RaidKind.Luke } current || current.Actor != actor ||
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

    public AntonRaidBattleView? RaidCombatView() => owner.Execute(() =>
        raidCombat is { } raid && raid.Actor.IsCurrent ? raid.Battle.View : null);

    public RaidResultDisplay? RaidCombatResult(AntonRaidBattleView view) => owner.Execute(() =>
        raidCombat is { } raid && raid.Actor.IsCurrent && raid.Battle.IsCurrent(view)
            ? raid.Battle.RewardPolicy(raid.Actor, view.Phase)?.Result : null);

    public bool CanPublishRaidBattle(RaidActor actor, AntonRaidBattleView view) => owner.Execute(() =>
        raidCombat is { } raid && raid.Actor == actor && actor.IsCurrent && raid.Battle.IsCurrent(view));

    // 117协调已提交死亡，不能由695、客户端symbol或复制的ClearFact创建阶段胜利。
    public AntonRaidBattleChange? CompleteRaidCombatDungeon(FormalDungeonReceipt receipt) => owner.Execute(() =>
    {
        if (raidCombat is not { } raid || raid.Battle.Kind == RaidKind.Luke || !raid.Actor.IsCurrent || formalDungeon is not { } dungeon ||
            receipt.Generation != owner.Generation || receipt.SelectionEpoch != SelectionEpoch ||
            receipt.Snapshot.Identity != dungeon.Run.Snapshot.Identity || dungeon.Run.ClearFact is not { } fact ||
            fact.Identity != receipt.Snapshot.Identity || !dungeon.Entry.IsTerminalMap(dungeon.Run.Snapshot.CurrentMap)) return null;
        var change = raid.Battle.CompleteDungeon(fact, DungeonIncountPolicy.Period((timeProvider ?? TimeProvider.System).GetUtcNow()).Week);
        if (change?.After.State == AntonRaidBattleState.Reward)
            raid.RewardWeek ??= DungeonIncountPolicy.Period((timeProvider ?? TimeProvider.System).GetUtcNow()).Week;
        return change;
    });

    public AntonRaidBattleChange? CompleteCurrentRaidCombatDungeon() => owner.Execute(() =>
        formalDungeon?.Run.ClearFact is not null ? CompleteRaidCombatDungeon(FormalReceipt()) : null);

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
        var battle = owner.Execute(() => raidCombat?.Battle);
        battle?.Publish(battle.View);
    }

    public RaidRewardReceipt? ClaimRaidReward(byte kind, byte card, IRaidRewardStore store,
        ItemCatalog items, GoldLimitCatalog gold, EpicPieceCatalog pieces, DateTimeOffset now)
    {
        var capture = owner.Execute(() =>
        {
            if (!Ready || Selected is not { } character || raidCombat is not { } raid || !raid.Actor.IsCurrent ||
                !Enum.IsDefined(raid.Battle.Kind)) return null;
            var view = raid.Battle.View;
            int phase = view.Phase;
            if (view.State != AntonRaidBattleState.Reward)
            {
                var old = raid.Receipts.Where(r => r.Key.Kind == kind).Select(r => r.Value).MaxBy(r => r.Prepared.Phase);
                return old is not null && old.Prepared.Card == card ? new RaidRewardCapture(raid, character, old.Prepared, old.Offer, old) : null;
            }
            var eligibility = raid.Battle.Kind == RaidKind.Luke ? raid.Battle.LukeEligibility(character.Id, phase) : null;
            if (raid.Battle.Kind == RaidKind.Luke && eligibility is not { Eligible: true }) return null;
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
