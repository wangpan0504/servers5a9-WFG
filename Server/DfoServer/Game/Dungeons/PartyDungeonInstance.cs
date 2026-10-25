using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal enum PartyDungeonNoticeKind { Selection, SelectionReturn, Entry, Loaded, Move, Death, Passive, Cinematic, Pickup, MemberLeft, Life, Pause, ResultSelection, ResultReturn, RetryEligibility, Departure }
internal sealed record PartyDungeonNotice(PartyDungeonNoticeKind Kind, PartyMember Recipient,
    PartyMember[] Members, FormalDungeonReceipt? Scene = null, DungeonSelectionReceipt? Selection = null,
    FormalDungeonDeathResult? Death = null, Character? Previous = null, FormalDungeonPickupResult? Pickup = null,
    ushort Peer = 0, DfoServer.Game.Town.TownTravelReceipt? Travel = null,
    byte State = 0, byte Option = 0, QuestLifecycleReceipt? QuestScene = null, int LegendDungeon = 0);

// 共享实例拥有全员加载屏障、路线推进与击杀事实；掉落随机、地面物品及拾取归各角色账本。
// 各Session的Run仅作为同一实例的角色账本投影，不能绕过此owner单独过图或发奖。
internal sealed class PartyDungeonInstance(Guid id, PartySnapshot party, IPartyDungeonStore store)
{
    [ThreadStatic] static PartyDungeonInstance? executing;
    readonly object gate = new();
    PartyMember[] members = party.Members;
    readonly HashSet<ushort> loaded = [];
    readonly HashSet<ushort> deaths = [];
    sealed class PendingDeath(ushort key, Func<GameSession, FormalDungeonDeathResult> observe)
    {
        public ushort Key { get; } = key;
        public Func<GameSession, FormalDungeonDeathResult> Observe { get; } = observe;
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
                try { return action(current); }
                finally { executing = previous; }
            }
        });
    }

    public PartyDungeonNotice[] CompleteLoading(GameSession reporter) => Execute(current =>
    {
        if (!current.Any(m => ReferenceEquals(m.Actor.Session, reporter)) || !reporter.CanCompletePartyLoading()) return [];
        loaded.Add(reporter.Selected!.Id);
        return ReleaseLoading(current);
    });

    PartyDungeonNotice[] ReleaseLoading(PartyMember[] current)
    {
        if (current.Any(m => !loaded.Contains(m.Actor.Character.Id))) return [];
        var notices = current.Select(m => new PartyDungeonNotice(PartyDungeonNoticeKind.Loaded, m, current,
            m.Actor.Session.CompleteFormalDungeonLoading() ?? m.Actor.Session.CurrentFormalDungeonScene() ??
                throw new InvalidOperationException("全员加载屏障释放失败"))).ToArray();
        loaded.Clear();
        return notices;
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
            !scene.Snapshot.LivingMonsters.SequenceEqual(leaderScene.Snapshot.LivingMonsters))) return [];
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
        var reportKey = (reporter.Selected!.Id,scene.Snapshot.CurrentMap,template,fingerprint);
        int ordinal = sequenced ? sceneReports.GetValueOrDefault(reportKey) : 0;
        var key = (scene.Snapshot.CurrentMap,template,fingerprint,ordinal);
        if (scenes.Contains(key))
        {
            if (sequenced) sceneReports[reportKey] = ordinal+1;
            return [];
        }
        if (pendingScene is not null && pendingScene.Key != key) return [];
        var pending = pendingScene ??= new(key,observe);
        foreach (var member in current)
        {
            ushort id = member.Actor.Character.Id;
            if (!pending.Completed.ContainsKey(id)) pending.Completed.Add(id,pending.Observe(member.Actor.Session));
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
        return notices;
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
        Func<GameSession, FormalDungeonDeathResult> observe) => Execute(current =>
    {
        if (!current.Any(m => ReferenceEquals(m.Actor.Session, reporter)) ||
            pendingScene is not null || current.Any(m => m.Actor.Session.Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement))) return [];
        var notices = new List<PartyDungeonNotice>();
        if (pendingDeath is not null) FinishPendingDeath(current, notices);
        if (deaths.Contains(key)) return notices.ToArray();
        pendingDeath = new(key, observe);
        FinishPendingDeath(current, notices);
        return notices.ToArray();
    });

    void FinishPendingDeath(PartyMember[] current, List<PartyDungeonNotice> notices)
    {
        var pending = pendingDeath!;
        foreach (var member in current)
        {
            if (pending.Completed.ContainsKey(member.Actor.Character.Id)) continue;
            var previous = member.Actor.Session.Selected;
            var result = pending.Observe(member.Actor.Session);
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
        return result;
    });

    public PartyDungeonNotice[] Detach(GameSession session)
    {
        ushort removed;
        lock (gate)
        {
            removed = members.FirstOrDefault(m => ReferenceEquals(m.Actor.Session, session))?.Actor.Character.Id ?? 0;
            members = members.Where(m => !ReferenceEquals(m.Actor.Session, session)).ToArray();
            session.RemovePartyDungeon(this);
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
