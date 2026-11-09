using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Game.Quests;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal enum PartyDungeonNoticeKind { RaidBattleSync, AntonHp, RaidSelection, LoadingReplay, Selection, SelectionReturn, Entry, Loaded, Move, Death, Passive, Cinematic, Pickup, MemberLeft, Life, Pause, ResultSelection, ResultReturn, RetryEligibility, Departure, EvildomWarStarted, EvildomWarUpdated, FiendwarTitles, AntonRejoinScene }
internal sealed record PartyDungeonNotice(PartyDungeonNoticeKind Kind, PartyMember Recipient,
    PartyMember[] Members, FormalDungeonReceipt? Scene = null, DungeonSelectionReceipt? Selection = null,
    FormalDungeonDeathResult? Death = null, Character? Previous = null, FormalDungeonPickupResult? Pickup = null,
    ushort Peer = 0, DfoServer.Game.Town.TownTravelReceipt? Travel = null,
    byte State = 0, byte Option = 0, QuestLifecycleReceipt? QuestScene = null, int LegendDungeon = 0, DfoServer.Game.Raids.AntonRaidBattleChange? RaidBattleChange = null,
    DfoServer.Game.Raids.RaidStageFactKind? RaidEvent = null,
    EvildomWarView? WarView = null, bool SceneTransition = false, AntonHpDisplay? AntonHp = null)
{
    public ImmutableArray<ushort> AntonRejoinDeaths { get; init; } = [];
    public ImmutableArray<EvildomWarPartyReward> EvildomWarRewards { get; init; } = [];
}

internal sealed record AntonHpDisplay(ushort Squad, ImmutableArray<ushort> Members, uint UsedCoins, uint Context, ImmutableArray<uint> Values);

internal sealed class PartySettlementCards
{
    readonly DungeonCardView?[] cards = new DungeonCardView?[8];
    public void Record(DungeonCardView view) => cards[view.Slot] = view;
    public DungeonCardView?[] Snapshot() => (DungeonCardView?[])cards.Clone();
    public bool CanPublish(IReadOnlyList<DungeonCardView?> snapshot)
    {
        if (snapshot.Count != cards.Length) return false;
        for (int slot = 0; slot < cards.Length; slot++)
        {
            if (snapshot[slot] is not { } previous) continue;
            if (previous.Slot != slot || cards[slot] is not { } current) return false;
            if (previous.FreeSelected && (!current.FreeSelected || previous.FreeChoice != current.FreeChoice ||
                previous.Free != current.Free || previous.BlackDiamond != current.BlackDiamond)) return false;
            if (previous.Paid is not null && (previous.PaidChoice != current.PaidChoice || previous.Paid != current.Paid)) return false;
        }
        return true;
    }
    public void Clear() => Array.Clear(cards);
}

// 共享实例拥有全员加载屏障、路线推进与击杀事实；掉落随机、地面物品及拾取归各角色账本。
// 各Session的Run仅作为同一实例的角色账本投影，不能绕过此owner单独过图或发奖。
internal sealed partial class PartyDungeonInstance(Guid id, PartySnapshot party, IPartyDungeonStore store)
{
    [ThreadStatic] static PartyDungeonInstance? executing;
    readonly object gate = new();
    readonly object cardGate = new();
    PartySettlementCards? settlementCards;
    public void StartCards(GameSession session)
    {
        lock (cardGate)
            if (ReferenceEquals(session.PartyDungeon, this) && Members.Any(member => ReferenceEquals(member.Actor.Session, session)))
                settlementCards ??= new PartySettlementCards();
    }
    public DungeonCardView?[] CardSnapshot()
    {
        lock (cardGate) return settlementCards?.Snapshot() ?? new DungeonCardView?[8];
    }
    public bool IsCardSnapshotCurrent(IReadOnlyList<DungeonCardView?> snapshot)
    {
        lock (cardGate) return settlementCards is not null && settlementCards.Snapshot().SequenceEqual(snapshot);
    }
    public bool CanPublishCardSnapshot(IReadOnlyList<DungeonCardView?> snapshot)
    {
        lock (cardGate) return settlementCards?.CanPublish(snapshot) == true;
    }
    public DungeonCardView?[] RecordCard(GameSession session, DungeonCardView view)
    {
        lock (cardGate)
        {
            if (!ReferenceEquals(session.PartyDungeon, this) || !Members.Any(member =>
                ReferenceEquals(member.Actor.Session, session) && member.Slot == view.Slot)) return CardSnapshot();
            settlementCards ??= new PartySettlementCards();
            settlementCards.Record(view);
            return settlementCards.Snapshot();
        }
    }
    internal void ClearCards()
    {
        lock (cardGate)
        {
            settlementCards?.Clear();
            settlementCards = null;
        }
    }
    readonly QuestMarriagePartyClear? marriageParty = FreezeMarriageParty(id, party.Members);
    bool marriagePartyInvalid;
    PartyMember[] members = party.Members;
    readonly HashSet<ushort> loaded = [];
    readonly HashSet<ushort> deaths = [];
    sealed class PendingDeath(ushort key, Func<GameSession, FormalDungeonDeathResult> observe,
        Func<GameSession, AntonRaidBattle.RaidDeathEligibility?>? captureRaidEligibility,
        Func<GameSession, AntonRaidBattle.RaidDeathEligibility?, FormalDungeonDeathResult>? observeRaidDeath)
    {
        public ushort Key { get; } = key;
        public Func<GameSession, FormalDungeonDeathResult> Observe { get; } = observe;
        public Func<GameSession, AntonRaidBattle.RaidDeathEligibility?>? CaptureRaidEligibility { get; } = captureRaidEligibility;
        public Func<GameSession, AntonRaidBattle.RaidDeathEligibility?, FormalDungeonDeathResult>? ObserveRaidDeath { get; } = observeRaidDeath;
        public Dictionary<ushort, AntonRaidBattle.RaidDeathEligibility?> RaidEligibilities { get; } = [];
        public bool RaidEligibilitiesCaptured { get; set; }
        public Dictionary<ushort, PartyDungeonNotice> Completed { get; } = [];
    }
    PendingDeath? pendingDeath;
    sealed class PendingScene((DungeonMap, uint, string, int) key, Func<GameSession, QuestLifecycleReceipt?> observe)
    {
        public (DungeonMap, uint, string, int) Key { get; } = key;
        public Func<GameSession, QuestLifecycleReceipt?> Observe { get; } = observe;
        public Dictionary<ushort, QuestLifecycleReceipt?> Completed { get; } = [];
    }
    readonly HashSet<(DungeonMap, uint, string, int)> scenes = [];
    readonly Dictionary<(ushort, DungeonMap, uint, string), int> sceneReports = [];
    PendingScene? pendingScene;
    bool? epicPieceShopAvailable;
    NpcFavorAppearance? favorNpc;
    public NpcFavorAppearance? ReadNpcFavorAppearance() => Execute(_ => favorNpc);
    public bool ConfirmNpcFavorAppearance(GameSession source, GameSession target, NpcFavorDungeonCapture capture,
        NpcFavorAppearance appearance) => Execute(current =>
    {
        if (current.SingleOrDefault(member => member.Slot == Leader)?.Actor.Session != source ||
            !current.Any(member => ReferenceEquals(member.Actor.Session, target)) ||
            favorNpc is { } prior && prior != appearance || target.CaptureNpcFavorDungeon() is not { } destination ||
            destination.Snapshot.Identity.InstanceId != capture.Snapshot.Identity.InstanceId ||
            destination.Snapshot.CurrentMap != capture.Snapshot.CurrentMap ||
            !source.CanConfirmNpcFavorAppearance(capture, appearance) || !target.CanConfirmNpcFavorAppearance(destination, appearance)) return false;
        if (!source.ConfirmNpcFavorAppearance(capture, appearance) || !target.ConfirmNpcFavorAppearance(destination, appearance))
            throw new InvalidOperationException("共享副本好感NPC已核验的快照发生变化");
        favorNpc = appearance;
        return true;
    });
    internal bool ResolveEpicPieceShop(Func<bool> roll)
    {
        if (!IsExecuting) throw new InvalidOperationException("碎片商人出场必须由当前共享副本owner冻结");
        epicPieceShopAvailable ??= roll();
        return epicPieceShopAvailable.Value;
    }
    sealed record CinematicCompletion(uint Quest, DungeonMap Map);
    CinematicCompletion? cinematicCompletion;
    public bool HasCompletedCinematic(uint quest, DungeonMap map) => Volatile.Read(ref cinematicCompletion) is { } completed &&
        completed.Quest == quest && completed.Map == map;
    public bool CanRetryCosmoDungeon() => Execute(current => current.All(m =>
        m.Actor.Session.FormalDungeonResultFocus() is { } scene && m.Actor.Session.CanRetryCosmoDungeon(scene,partyAdmission:true)));
    public Guid Id { get; } = id;
    public bool IsExecuting => ReferenceEquals(executing, this);
    public PartyMember[] Members => Volatile.Read(ref members);
    public byte Leader => Members.Any(m => m.Slot == party.Leader) ? party.Leader : Members.Select(m => m.Slot).DefaultIfEmpty((byte)0).Min();

    public T Execute<T>(Func<PartyMember[], T> action)
    {
        var current = Members.Where(m => m.IsCurrent() && ReferenceEquals(m.Actor.Session.PartyDungeon, this)).ToArray();
        if (current.Length == 0) throw new InvalidOperationException("Party dungeon has no current participants");
        return AccountOwners.Lease.ExecuteGroup(current.Select(m => (m.Actor.Owner, m.Actor.Character.Id)).ToArray(), () =>
        {
            lock (gate)
            {
                if (current.Any(m => !m.IsCurrent() || !members.Contains(m) || !ReferenceEquals(m.Actor.Session.PartyDungeon, this)))
                    throw new InvalidOperationException("Party dungeon participant changed");
                var previous = executing;
                executing = this;
                try
                {
                    ValidateMarriageParty(current);
                    return action(current);
                }
                finally { executing = previous; }
            }
        });
    }

    AntonHpDisplay? antonHp;
    readonly AntonDungeonRevivalPool antonRevival = new();
    internal AntonDungeonRevivalPool.Reservation? ReserveAntonRevival(GameSession session,
        DungeonRunIdentity identity, long death, int? limit)
    {
        // 本人owner已持有；独立额度锁不获取其他成员owner或共享场景gate，保持原锁序。
        if (identity.InstanceId != Id || !ReferenceEquals(session.PartyDungeon, this) ||
            !Members.Any(member => ReferenceEquals(member.Actor.Session, session) && member.IsCurrent() &&
                member.Actor.Character.Id == identity.CharacterId)) return null;
        return antonRevival.Reserve(identity, death, limit);
    }
    internal bool IsAntonHpCurrent(AntonHpDisplay display) => ReferenceEquals(Volatile.Read(ref antonHp), display) &&
        Members.Length == display.Members.Length && Members.All(member => member.IsCurrent() &&
            display.Members.Contains(member.Actor.Character.Id));

    internal PartyDungeonNotice[] ReportAntonHp(GameSession reporter, uint context, uint[] values) => Execute(current =>
    {
        if (pendingDeath is not null || pendingScene is not null || loaded.Count != 0 ||
            !current.Any(member => ReferenceEquals(member.Actor.Session, reporter)) || values.Length == 0 ||
            values.Any(value => value > 100)) return [];
        var captured = current.Select(member => member.Actor.Session.CaptureAntonHp()).ToArray();
        if (captured.Any(row => row is null)) return [];
        var first = captured[0]!.Value;
        if (captured.Any(row => row!.Value.Scene.Snapshot.Identity.InstanceId != Id ||
            row.Value.Squad != first.Squad || row.Value.Scene.Snapshot.CurrentMap != first.Scene.Snapshot.CurrentMap ||
            row.Value.MonitorCount is { } count && values.Length != count)) return [];
        // 只保留本Run最近一次C及同顺序百分比，不为客户端任意C建立无界字典；不授予死亡或结算权限。
        var display = new AntonHpDisplay(first.Squad, [..current.OrderBy(member => member.Slot)
            .Select(member => member.Actor.Character.Id)], checked((uint)antonRevival.Used), context, [..values]);
        Volatile.Write(ref antonHp, display);
        return current.Select((member, index) => new PartyDungeonNotice(PartyDungeonNoticeKind.AntonHp, member,
            current, captured[index]!.Value.Scene, AntonHp: display)).ToArray();
    });

    internal bool ReportAntonEnergy(GameSession reporter) => Execute(current =>
        pendingDeath is null && pendingScene is null && loaded.Count == 0 &&
        current.Any(member => ReferenceEquals(member.Actor.Session, reporter)) &&
        current.All(member => member.Actor.Session.CanMoveWithParty()) && reporter.ReportAntonEnergy());

    internal DfoServer.Game.Raids.AntonRaidBattleChange? ReportFiendwarBoss(GameSession reporter, uint operation, uint value) => Execute(current =>
        pendingDeath is null && pendingScene is null && loaded.Count == 0 &&
        current.Any(member => ReferenceEquals(member.Actor.Session, reporter)) &&
        current.All(member => member.Actor.Session.CanMoveWithParty()) ? reporter.ReportFiendwarBoss(operation, value) : null);

    public LukeFacilityReportReceipt? ReportLukeFacility(GameSession reporter, int symbol, uint value, byte operation) => Execute(current =>
    {
        if (pendingDeath is not null || pendingScene is not null || loaded.Count != 0 ||
            !current.Any(member => ReferenceEquals(member.Actor.Session, reporter)) ||
            current.Any(member => !member.Actor.Session.CanMoveWithParty())) return null;
        return reporter.ReportLukeFacility(symbol, value, operation);
    });

    static QuestMarriagePartyClear? FreezeMarriageParty(Guid instance, PartyMember[] members)
    {
        if (members.Length != 2) return null;
        var first = members[0].Actor.Character;
        var second = members[1].Actor.Character;
        return first.Marriage is { State: >= 1 and <= 3 } relation && second.Marriage?.Id == relation.Id &&
            first.Id != second.Id && relation.IsMember(first.Id) && relation.IsMember(second.Id)
            ? new(instance, relation.Id, first.Id, second.Id) : null;
    }

    void ValidateMarriageParty(PartyMember[] current)
    {
        if (marriageParty is { } frozen && (current.Length != 2 || current.Any(member =>
            member.Actor.Character.Id != frozen.FirstId && member.Actor.Character.Id != frozen.SecondId ||
            member.Actor.Session.Selected?.Marriage is not { State: >= 1 and <= 3 } relation || relation.Id != frozen.MarriageId)))
            marriagePartyInvalid = true;
        foreach (var member in current)
            member.Actor.Session.SetMarriageDungeonWitness(this, marriagePartyInvalid ? null : marriageParty);
    }

    public PartyDungeonNotice[] CompleteLoading(GameSession reporter) => Execute(current =>
    {
        if (TryCompleteAntonRejoinLoading(reporter, current) is { } restored) return restored;
        if (!current.Any(m => ReferenceEquals(m.Actor.Session, reporter)) || !reporter.CanCompletePartyLoading()) return [];
        loaded.Add(reporter.Selected!.Id);
        return ReleaseLoading(current);
    });

    PartyDungeonNotice[] ReleaseLoading(PartyMember[] current)
    {
        if (current.Any(m => !loaded.Contains(m.Actor.Character.Id))) return [];
        var notices = current.Select(m =>
        {
            bool rejoining = m.Actor.Session.HasAntonRejoinPresentation;
            var scene = m.Actor.Session.CompleteAntonRejoinPresentation() ?? m.Actor.Session.CompleteFormalDungeonLoading() ??
                m.Actor.Session.CurrentFormalDungeonScene() ?? throw new InvalidOperationException("全员加载屏障释放失败");
            return rejoining ? AntonRejoinLoadedNotice(m, current, scene) : new PartyDungeonNotice(PartyDungeonNoticeKind.Loaded, m, current, scene);
        }).ToArray();
        loaded.Clear();
        // 震颤等终点在全员完成加载后才生成ClearFact，不能只在死亡请求路径登记团本贡献。
        var completed = notices.ToList();
        if (notices.Any(notice => notice.Scene is { Entry.RaidEntry: not null, Snapshot.Phase: SinglePlayerDungeonPhase.Settlement }))
            AppendBattleNotices(current, completed);
        return GameSession.ObserveRaidPartyRun(current, Id, completed.ToArray(), observeMap: true);
    }

    public PartyDungeonNotice[] Move(GameSession reporter, byte x, byte y, byte kind) => Execute(current =>
    {
        if (!current.Any(m => ReferenceEquals(m.Actor.Session, reporter)) || kind > 1 || pendingDeath is not null || pendingScene is not null ||
            reporter.CurrentDungeonDeathReturn() is not null ||
            current.Any(m => !m.Actor.Session.CanMoveWithParty())) return [];
        // 先以共享领队投影验证门墙与清房，其余投影必须与其同房、同死亡集。
        var leaderScene = reporter.CurrentFormalDungeonScene();
        if (leaderScene is null || current.Any(m => m.Actor.Session.CurrentFormalDungeonScene() is not { } scene ||
            scene.Snapshot.CurrentMap != leaderScene.Snapshot.CurrentMap ||
            !scene.Snapshot.LivingMonsters.SequenceEqual(leaderScene.Snapshot.LivingMonsters)) ||
            current.Any(member => !member.Actor.Session.CanMoveEvildomWarChallengeWithParty(x, y, kind))) return [];
        if (reporter.MoveFormalDungeon(x, y, kind) is not { } first) return [];
        var notices = current.Select(m => new PartyDungeonNotice(PartyDungeonNoticeKind.Move, m, current,
            ReferenceEquals(m.Actor.Session, reporter) ? first : m.Actor.Session.MoveFormalDungeon(x, y, kind) ??
                throw new InvalidOperationException("共享房间推进的角色投影不一致"))).ToArray();
        loaded.Clear();
        return notices;
    });

    public bool OperateRidable(GameSession reporter, byte operation, uint resource, byte x, byte y) => Execute(current =>
    {
        if (!current.Any(m => ReferenceEquals(m.Actor.Session, reporter)) || pendingScene is not null || pendingDeath is not null ||
            current.Any(m => !m.Actor.Session.CanMoveWithParty())) return false;
        if (reporter.OperateFormalRidable(operation,resource,x,y)?.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate)) return false;
        foreach (var member in current.Where(m => !ReferenceEquals(m.Actor.Session,reporter)))
            if (member.Actor.Session.OperateFormalRidable(operation,resource,x,y)?.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate))
                throw new InvalidOperationException("共享骑乘操作的角色投影不一致");
        return true;
    });

    public PartyDungeonNotice[] ObservePassive(GameSession reporter, uint template, string fingerprint,
        Func<GameSession, QuestLifecycleReceipt?> observe, bool sequenced = false) => Execute(current =>
    {
        if (!current.Any(m => ReferenceEquals(m.Actor.Session,reporter)) || pendingDeath is not null ||
            reporter.CurrentFormalDungeonScene() is not { } scene) return [];
        bool energy = current.Any(member => member.Actor.Session.IsLukeEnergyDungeon);
        if (energy && (loaded.Count != 0 || current.Any(member => !member.Actor.Session.IsLukeEnergyDungeon ||
            member.Actor.Session.CurrentFormalDungeonScene() is not { Snapshot.Phase: SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement } loadedScene ||
            loadedScene.Snapshot.CurrentMap != scene.Snapshot.CurrentMap))) return [];
        var reportKey = (reporter.Selected!.Id,scene.Snapshot.CurrentMap,template,fingerprint);
        int ordinal = sequenced ? sceneReports.GetValueOrDefault(reportKey) : 0;
        var key = (scene.Snapshot.CurrentMap,template,fingerprint,ordinal);
        if (scenes.Contains(key))
        {
            if (sequenced) sceneReports[reportKey] = ordinal+1;
            var repeated = new List<PartyDungeonNotice>();
            AppendBattleNotices(current, repeated);
            return repeated.ToArray();
        }
        if (pendingScene is not null && pendingScene.Key != key) return [];
        var pending = pendingScene ??= new(key,observe);
        foreach (var member in current)
        {
            ushort id = member.Actor.Character.Id;
            if (!pending.Completed.ContainsKey(id))
            {
                var receipt = pending.Observe(member.Actor.Session);
                // 能源图必须全员持久化；SQL失败保留已提交成员，重放只补剩余事务。
                if (!energy || receipt is not null) pending.Completed.Add(id, receipt);
            }
        }
        if (energy && pending.Completed.Count != current.Length)
        {
            // 无成员提交的非法报告不得占住事件；部分提交仍保留原候选供重放补齐。
            if (pending.Completed.Count == 0) pendingScene = null;
            return [];
        }
        var notices = current.Where(m => pending.Completed[m.Actor.Character.Id] is not null).Select(m =>
            new PartyDungeonNotice(PartyDungeonNoticeKind.Passive,m,current,m.Actor.Session.CurrentFormalDungeonScene(),
                QuestScene: pending.Completed[m.Actor.Character.Id])).ToArray();
        if (notices.Length > 0)
        {
            scenes.Add(key);
            if (sequenced) sceneReports[reportKey] = ordinal+1;
        }
        pendingScene = null;
        var completed = new List<PartyDungeonNotice>(notices);
        AppendBattleNotices(current, completed);
        return completed.ToArray();
    });

    public PartyDungeonNotice[] CompleteCinematic(GameSession reporter, ushort quest, QuestLifecycleReceipt receipt) => Execute(current =>
    {
        if (!current.Any(m => ReferenceEquals(m.Actor.Session,reporter)) || receipt.Outcome == QuestLifecycleOutcome.Rejected ||
            !reporter.CanPublishQuestLifecycle(receipt) || !receipt.Character.ActiveMainQuests.Any(q => q.QuestId==quest && q.ConditionState==0) ||
            reporter.CurrentFormalDungeonScene() is not { } scene || scene.Entry.RouteQuestId != quest ||
            !scene.Entry.QuestAcceptances.Any(q => q.QuestId==quest && receipt.Character.ActiveMainQuests.Any(active =>
                active.QuestId==q.QuestId && active.Slot==q.Slot && active.AcceptanceId==q.AcceptanceId)) ||
            current.Any(m => m.Actor.Session.CurrentFormalDungeonScene()?.Snapshot.CurrentMap != scene.Snapshot.CurrentMap)) return [];
        Volatile.Write(ref cinematicCompletion,new(quest,scene.Snapshot.CurrentMap));
        return current.Select(m => new PartyDungeonNotice(PartyDungeonNoticeKind.Cinematic,m,current,m.Actor.Session.CurrentFormalDungeonScene())).ToArray();
    });

    public PartyDungeonNotice[] ObserveDeath(GameSession reporter, ushort key,
        Func<GameSession, FormalDungeonDeathResult> observe, Func<GameSession, bool>? prepareDynamic = null,
        Func<GameSession, AntonRaidBattle.RaidDeathEligibility?>? captureRaidEligibility = null,
        Func<GameSession, AntonRaidBattle.RaidDeathEligibility?, FormalDungeonDeathResult>? observeRaidDeath = null) => Execute(current =>
    {
        if (!current.Any(m => ReferenceEquals(m.Actor.Session, reporter)) ||
            pendingScene is not null || current.Any(m => m.Actor.Session.Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement))) return [];
        if (current.Any(member => member.Actor.Session.HasCurrentFiendwarDynamic(key)))
        {
            if (pendingDeath is not null || prepareDynamic is null ||
                current.Any(member => !prepareDynamic(member.Actor.Session))) return [];
            var retired = new List<PartyDungeonNotice>();
            foreach (var member in current)
            {
                var previous = member.Actor.Session.Selected;
                var result = observe(member.Actor.Session);
                if (result.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate) ||
                    !result.SpecialChildRetired || result.Receipt is null)
                    throw new InvalidOperationException("已全员核验的漩涡动态死亡不能部分退休");
                retired.Add(new(PartyDungeonNoticeKind.Death, member, current, result.Receipt, Death: result, Previous: previous));
            }
            // 动态死亡只补零奖励38，不进入静态击杀集合或Raid通关检查；同包可重放退休投影。
            return retired.ToArray();
        }
        var notices = new List<PartyDungeonNotice>();
        if (pendingDeath is not null) FinishPendingDeath(current, notices);
        if (deaths.Contains(key))
        {
            AppendBattleNotices(current, notices);
            return notices.ToArray();
        }
        pendingDeath = new(key, observe, captureRaidEligibility, observeRaidDeath);
        FinishPendingDeath(current, notices);
        AppendBattleNotices(current, notices);
        return notices.ToArray();
    });

    void AppendBattleNotices(PartyMember[] current, List<PartyDungeonNotice> notices)
    {
        if (GameSession.CompleteRaidPartyDungeon(current, Id, out var change))
            // 仅传播已提交事实；重复死亡可重同步当前view，但绝不再次推进阶段。
            notices.Add(new(PartyDungeonNoticeKind.RaidBattleSync, current[0], current, current[0].Actor.Session.CurrentFormalDungeonScene(), RaidBattleChange: change));
        AppendEvildomWarNotices(current, notices);
    }

    void AppendEvildomWarNotices(PartyMember[] current, List<PartyDungeonNotice> notices)
    {
        if (GameSession.CompleteEvildomWarPartyDungeon(current, Id) is { } war)
            notices.AddRange(PartyDungeonService.EvildomWarNotices(current, war));
    }

    void FinishPendingDeath(PartyMember[] current, List<PartyDungeonNotice> notices)
    {
        var pending = pendingDeath!;
        if (!pending.RaidEligibilitiesCaptured && pending.CaptureRaidEligibility is { } capture)
        {
            foreach (var member in current)
                if (!pending.RaidEligibilities.ContainsKey(member.Actor.Character.Id))
                    pending.RaidEligibilities.Add(member.Actor.Character.Id, capture(member.Actor.Session));
            pending.RaidEligibilitiesCaptured = true;
        }
        foreach (var member in current)
        {
            if (pending.Completed.ContainsKey(member.Actor.Character.Id)) continue;
            var previous = member.Actor.Session.Selected;
            var result = pending.ObserveRaidDeath is { } observeRaid && pending.RaidEligibilities.TryGetValue(member.Actor.Character.Id, out var eligibility)
                ? observeRaid(member.Actor.Session, eligibility) : pending.Observe(member.Actor.Session);
            if (result.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate))
            {
                if (pending.Completed.Count == 0) { pendingDeath = null; return; }
                throw new InvalidOperationException("共享死亡的角色账本状态不一致");
            }
            pending.Completed.Add(member.Actor.Character.Id,
                new(PartyDungeonNoticeKind.Death, member, current, result.Receipt, Death: result, Previous: previous));
        }
        notices.AddRange(current.Select(m => pending.Completed[m.Actor.Character.Id]).Where(n => n.Death?.Receipt is not null));
        deaths.Add(pending.Key);
        pendingDeath = null;
        RecordAntonConstructorDeath(current, notices, pending.Key);
    }

    public PartyDungeonNotice[] Pickup(GameSession reporter, uint key,
        Func<GameSession, FormalDungeonPickupResult> pickup) => Execute<PartyDungeonNotice[]>(current =>
    {
        var member = current.SingleOrDefault(m => ReferenceEquals(m.Actor.Session, reporter));
        if (pendingDeath is not null || pendingScene is not null || member is null) return [];
        // 同一个地面key在每名队员账本中独立存在，只消费和投影发起者的冻结物品。
        var result = pickup(reporter);
        return [new(PartyDungeonNoticeKind.Pickup, member, current,
            reporter.CurrentFormalDungeonScene(), Pickup: result, Peer: member.Actor.Character.Id)];
    });

    public PartyDungeonNotice[] ResultSelection(GameSession leader) => Execute(current =>
    {
        if (!current.Any(m => m.Slot == Leader && ReferenceEquals(m.Actor.Session, leader)) ||
            current.Any(m => m.Actor.Session.FormalDungeonResultFocus() is null) || pendingDeath is not null || pendingScene is not null) return [];
        var result = PartyDungeonService.Exit(store, current, PartyDungeonExitKind.ResultSelection);
        if (result.Length == 0) return [];
        foreach (var member in current) member.Actor.Session.RemovePartyDungeon(this);
        members = [];
        ClearCards();
        return result;
    });

    public PartyDungeonNotice[] ClearedHellSelection(GameSession leader, uint dungeon) => Execute(current =>
    {
        if (!current.Any(m => m.Slot == Leader && ReferenceEquals(m.Actor.Session,leader)) ||
            pendingDeath is not null || pendingScene is not null ||
            current.Any(m => !m.Actor.Session.CanBeginClearedHellSelection(dungeon))) return [];
        var result = PartyDungeonService.Exit(store, current, PartyDungeonExitKind.HellSelection);
        if (result.Length == 0) return [];
        foreach (var member in current) member.Actor.Session.RemovePartyDungeon(this);
        members = [];
        ClearCards();
        return result;
    });

    public PartyDungeonNotice[] ResultReturn(GameSession leader) => Execute(current =>
    {
        if (!current.Any(m => m.Slot == Leader && ReferenceEquals(m.Actor.Session, leader)) ||
            current.Any(m => m.Actor.Session.FormalDungeonResultFocus() is null) || pendingDeath is not null || pendingScene is not null) return [];
        var result = PartyDungeonService.Exit(store, current, PartyDungeonExitKind.ResultReturn);
        if (result.Length == 0) return [];
        foreach (var member in current) member.Actor.Session.RemovePartyDungeon(this);
        members = [];
        ClearCards();
        return result;
    });

    public PartyDungeonNotice[] RetreatEvildomWar(GameSession leader) => Execute(current =>
    {
        if (!current.Any(m => m.Slot == Leader && ReferenceEquals(m.Actor.Session, leader)) ||
            leader.ReadEvildomWar() is not { } war || pendingDeath is not null || pendingScene is not null ||
            current.Any(m => !m.Actor.Session.CanRetreatEvildomWar() ||
                !ReferenceEquals(m.Actor.Session.ReadEvildomWar()?.Battle, war.Battle))) return [];
        // 战况表撤退保留队伍与战役；全员按原退场事务提交后才释放各自Run和发布等待区。
        var result = PartyDungeonService.Exit(store, current, PartyDungeonExitKind.EvildomWarRetreat);
        if (result.Length == 0) return [];
        foreach (var member in current) member.Actor.Session.RemovePartyDungeon(this);
        members = [];
        ClearCards();
        return result;
    });

    // 恢复退场已由同一固定owner组整体提交，不能逐人Detach后再以缩小的组合重入Execute。
    internal void CompleteFiendwarKeyRecovery(PartyMember[] exited)
    {
        if (!IsExecuting || exited.Any(member => !members.Contains(member) ||
            member.Actor.Session.Phase != SessionPhase.Town))
            throw new InvalidOperationException("漩涡恢复退场必须在原固定组提交后完成");
        foreach (var member in exited) member.Actor.Session.RemovePartyDungeon(this);
        members = members.Except(exited).ToArray();
        if (members.Length == 0) ClearCards();
    }

    public PartyDungeonNotice[] Detach(GameSession session)
    {
        ushort removed;
        lock (gate)
        {
            session.SetMarriageDungeonWitness(this, null);
            marriagePartyInvalid = true;
            removed = members.FirstOrDefault(m => ReferenceEquals(m.Actor.Session, session))?.Actor.Character.Id ?? 0;
            members = members.Where(m => !ReferenceEquals(m.Actor.Session, session)).ToArray();
            session.RemovePartyDungeon(this);
            if (members.Length == 0) ClearCards();
        }
        if (removed == 0 || !Members.Any(m => m.IsCurrent() && ReferenceEquals(m.Actor.Session.PartyDungeon, this))) return [];
        return Execute(current =>
        {
            var notices = current.Select(m => new PartyDungeonNotice(PartyDungeonNoticeKind.MemberLeft, m, current, Peer: removed)).ToList();
            if (current.All(m => m.Actor.Session.Phase == SessionPhase.Loading)) notices.AddRange(ReleaseLoading(current));
            return notices.ToArray();
        });
    }
}
