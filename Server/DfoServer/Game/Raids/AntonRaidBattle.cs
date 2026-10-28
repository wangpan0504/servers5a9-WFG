using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal enum AntonRaidBattleState : byte { Fighting = 2, Reward = 3, Completed = 4 }
internal sealed record AntonRaidBattleView(Guid Instance, long Revision, int Phase, AntonRaidBattleState State,
    ImmutableDictionary<string, int> Symbols, ImmutableDictionary<int, RaidDungeonState> Dungeons,
    ImmutableDictionary<RaidTimer, int> Timers)
{
    public ImmutableDictionary<int, RaidClause> Deferred { get; init; } = ImmutableDictionary<int, RaidClause>.Empty;
    public ImmutableDictionary<int, long> DeferredAnchors { get; init; } = ImmutableDictionary<int, long>.Empty;
    public ImmutableDictionary<int, long> DeferredGenerations { get; init; } = ImmutableDictionary<int, long>.Empty;
    public ImmutableDictionary<int, long> DungeonGenerations { get; init; } = ImmutableDictionary<int, long>.Empty;
    public ImmutableDictionary<RaidTimer, long> TimerGenerations { get; init; } = ImmutableDictionary<RaidTimer, long>.Empty;
    public long TimerSequence { get; init; }
    public byte? LukePhaseCoinsUsed { get; init; }
    public ImmutableDictionary<int, LukeRouteProgress> LukeRoutes { get; init; } = ImmutableDictionary<int, LukeRouteProgress>.Empty;
    // 仅显式设施报告兼容模式使用，键最多为三个已证设施符号；随候选原子提交。
    public ImmutableDictionary<string, long> LukeFacilityWriteEpochs { get; init; } = ImmutableDictionary<string, long>.Empty;
    public bool ClockFaulted { get; init; }
    public bool PhaseExpired { get; init; }
    public long PhaseClockGeneration { get; init; }
    public ImmutableDictionary<RaidTimer, long> TimerAnchors { get; init; } = ImmutableDictionary<RaidTimer, long>.Empty;
}
internal enum RaidStageFactKind { EnterDungeon, EnterMap, GiveUpDungeon, PhaseEnd }
// 只由已复验的正式Run或阶段deadline构造；不接客户端任意symbol/地图号。
internal readonly record struct RaidStageFact(RaidStageFactKind Kind, int Phase, int Dungeon = 0, int Map = 0);
internal sealed record AntonRaidBattleChange(AntonRaidBattleView Before, AntonRaidBattleView After);

// 正式实例的阶段聚合。输入只有已绑定副本的清图事实、服务端计时器和服务端机制结果；不执行客户端 SET。
internal sealed partial class AntonRaidBattle
{
    readonly object gate = new();
    readonly RaidScript script;
    sealed record DungeonBinding(DungeonRunIdentity Identity, int Phase, uint Dungeon, RaidDungeonRun? Run = null);
    readonly Dictionary<(Guid Run, ushort Character), DungeonBinding> entries = [];
    readonly Dictionary<RaidActor, ImmutableArray<RaidPhaseRewardPolicy>> members = [];
    readonly HashSet<(ushort Character, int Phase, byte Kind)> rewards = [];
    AntonRaidBattleView current;
    int? rewardWeek;
    public Guid Instance { get; }
    public RaidKind Kind { get; }
    public int PhaseCount => script.Phases.Length;
    public int? RewardWeek { get { lock (gate) return rewardWeek; } }
    public AntonRaidBattleView View { get { lock (gate) return current; } }

    public bool Join(RaidActor actor, ImmutableArray<RaidPhaseRewardPolicy> policy)
    {
        lock (gate)
        {
            if (!actor.IsCurrent || current.Revision != 0 || members.Count >= (Kind == RaidKind.Luke ? 8 : 20) || policy.Length != PhaseCount ||
                policy.Any(p => string.IsNullOrWhiteSpace(p.Source) || p.Offers.IsEmpty ||
                    p.Offers.Any(o => o.Kind is not (0 or 1) || o.Grants.IsEmpty) ||
                    p.Offers.Select(o => (o.Kind, o.Card)).Distinct().Count() != p.Offers.Length ||
                    p.Offers.Any(o => o.Display.Length > byte.MaxValue || o.Display.Any(r => r.Items.Length > byte.MaxValue) ||
                        2 + o.Display.Sum(r => 4 + 8 * r.Items.Length) > 65520)) ||
                policy.Where((p, index) => p.Result.Phase != index || p.Result.Result is not (0 or 2)).Any() ||
                members.Keys.Any(m => m.Character == actor.Character)) return false;
            members.Add(actor, policy); return true;
        }
    }

    internal bool JoinStart(RaidActor actor)
    {
        lock (gate)
        {
            if (!actor.IsCurrent || current.Revision != 0 || members.Count >= (Kind == RaidKind.Luke ? 8 : 20) ||
                members.Keys.Any(m => m.Character == actor.Character)) return false;
            if (lukeCompletion is { } completion)
            {
                if (!Join(actor, completion.Phases.Select(phase => phase.Rewards).ToImmutableArray())) return false;
            }
            else if (antonRewards is not null)
            {
                if (!Join(actor, antonRewards.Create(actor.Session.PremiumPcRoom))) return false;
                originalAntonMembers.Add(actor.Character, actor);
            }
            else members.Add(actor, []);
            if (Kind == RaidKind.Luke) originalLukeMembers.Add(actor.Character, actor);
            return true;
        }
    }

    // 精确移除原actor及其Run占用；不清新会话，不把离团当作领奖或退款。
    internal bool DetachStart(RaidActor actor)
    {
        lock (gate)
        {
            if (!members.Remove(actor)) return false;
            ReleaseDungeonActor(actor, null);
            if (Kind == RaidKind.Luke || HasAntonRewards)
            {
                ScheduleLukeClockLocked();
            }
            foreach (var key in entries.Where(row => row.Value.Identity.CharacterId == actor.Character).Select(row => row.Key).ToArray())
                entries.Remove(key);
            return true;
        }
    }

    public RaidPhaseRewardPolicy? RewardPolicy(RaidActor actor, int phase)
    { lock (gate) return actor.IsCurrent && members.TryGetValue(actor, out var policy) && phase >= 0 && phase < policy.Length ? policy[phase] : null; }

    public bool ConfirmReward(RaidActor actor, PreparedRaidReward prepared)
    {
        lock (gate)
        {
            if (!actor.IsCurrent || prepared.RaidType != Kind || !RequiresReward(actor, current.Phase) || prepared.Instance != Instance || prepared.Character != actor.Character ||
                prepared.Phase != current.Phase || current.State != AntonRaidBattleState.Reward || !members.TryGetValue(actor, out var policy) || policy.IsEmpty) return false;
            rewards.Add((actor.Character, current.Phase, prepared.Kind));
            return AllRewardsConfirmed(current.Phase);
        }
    }

    public void Publish(AntonRaidBattleView view, Action<Exception>? recipientFailure = null)
    {
        RaidActor[] recipients;
        lock (gate)
        {
            if (!ReferenceEquals(current, view)) return;
            recipients = members.Keys.ToArray();
        }
        foreach (var actor in recipients)
        {
            try { if (actor.IsCurrent) actor.Session.RaidBattleNoticeArrived?.Invoke(view, actor); }
            catch (Exception error) when (recipientFailure is not null) { recipientFailure(error); }
        }
    }

    public bool IsCurrent(AntonRaidBattleView view) { lock (gate) return ReferenceEquals(current, view); }

    internal (int Id, int Value)[] SymbolSnapshot(AntonRaidBattleView view) =>
        script.Symbols.OrderBy(row => row.Value).Select(row => (row.Value, view.Symbols[row.Key])).ToArray();
    internal (int Id, RaidDungeonState State)[] DungeonSnapshot(AntonRaidBattleView view) =>
        script.Phases.SelectMany(phase => phase.Dungeons).Select(d => d.Id).Distinct().Order()
            .Select(id => (id, view.Dungeons.GetValueOrDefault(id, RaidDungeonState.Hide))).ToArray();


    readonly bool antonPhaseProgress;
    internal bool PhaseProgress => antonPhaseProgress;

    public AntonRaidBattle(RaidScript script, Guid? instance = null, RaidKind kind = RaidKind.Anton,
        LukeRaidCompletionPolicy? completion = null, TimeProvider? clock = null, LukeRaidRevivalPolicy? revival = null, LukeRaidPhaseTimeoutPolicy? phaseTimeout = null,
        LukeFixedCardRoute? fixedRoute = null, bool antonPhaseProgress = false, LukeFacilityReportPolicy? facilities = null, AntonResourceRewards? antonRewards = null)
    {
        if (!script.Issues.IsEmpty || script.Phases.IsEmpty || script.Phases.Length > sbyte.MaxValue ||
            script.Symbols.Count > byte.MaxValue || script.Phases.Any(p => p.Dungeons.Length > byte.MaxValue))
            throw new InvalidDataException("攻坚阶段资源不完整或超出已证协议范围");
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (antonPhaseProgress && kind != RaidKind.Anton) throw new InvalidDataException("阶段推进开关只属于安徒恩");
        this.antonPhaseProgress = antonPhaseProgress;
        if (antonRewards is not null && (kind != RaidKind.Anton || !antonPhaseProgress))
            throw new InvalidDataException("安徒恩正式奖励要求正式阶段推进");
        this.antonRewards = antonRewards;
        if (completion is not null && (kind != RaidKind.Luke || !ValidLukeCompletion(completion)))
            throw new InvalidDataException("卢克阶段策略不完整");
        if (revival is not null && (kind != RaidKind.Luke || script.Phases.Length != 2 || string.IsNullOrWhiteSpace(revival.Source)))
            throw new InvalidDataException("卢克复活策略不完整");
        if (phaseTimeout is not null && (kind != RaidKind.Luke || !phaseTimeout.Valid(script.Phases.Length)))
            throw new InvalidDataException("卢克阶段超时兼容策略不完整");
        if (fixedRoute is not null && (kind != RaidKind.Luke || !fixedRoute.Matches(script)))
            throw new InvalidDataException("卢克固定路线与执行脚本不匹配");
        if (facilities is not null && (kind != RaidKind.Luke || !facilities.Valid))
            throw new InvalidDataException("卢克设施桥必须显式声明客户端动作报告信任合同");
        lukeFacilities = facilities;
        lukeFixedRoute = fixedRoute;
        lukePhaseTimeout = phaseTimeout;
        lukeRevival = revival;
        lukeCompletion = completion; lukeClock = clock ?? TimeProvider.System;
        Kind = kind;
        this.script = script;
        Instance = instance ?? Guid.NewGuid();
        if (Instance == Guid.Empty) throw new InvalidDataException("攻坚实例身份不能为空");
        current = ReconcileLukeRoutes(Initialize(0, 0));
    }

    AntonRaidBattleView Initialize(int phase, long revision, ImmutableDictionary<string, int>? inheritedSymbols = null, long timerSequence = 0)
    {
        var definition = script.Phases[phase];
        var symbols = inheritedSymbols ?? script.Symbols.Keys.ToImmutableDictionary(s => s, _ => 0);
        foreach (var dungeon in definition.Dungeons) symbols = symbols.SetItem(dungeon.HpSymbol, dungeon.HpRaw);
        return Evaluate(new(Instance, revision, phase, AntonRaidBattleState.Fighting, symbols,
            definition.Dungeons.ToImmutableDictionary(d => d.Id, d => d.InitialState),
            ImmutableDictionary<RaidTimer, int>.Empty)
            { LukePhaseCoinsUsed = SupportsLukeRevival && phase == 1 ? (byte)0 : null,
                TimerSequence = timerSequence, DungeonGenerations = Kind == RaidKind.Luke || HasAntonRewards
                ? definition.Dungeons.ToImmutableDictionary(d => d.Id, d => d.InitialState == RaidDungeonState.Open ? 1L : 0L)
                : ImmutableDictionary<int, long>.Empty }, null, null, null);
    }

    // 由会话在真正安装正式副本后调用；同一实例不能跨角色、阶段或副本重绑。
    public bool BindDungeon(DungeonRunIdentity identity, uint dungeon)
    {
        lock (gate)
        {
            if (current.State != AntonRaidBattleState.Fighting || !current.Dungeons.TryGetValue(checked((int)dungeon), out var state) ||
                state != RaidDungeonState.Open || identity.InstanceId == Guid.Empty) return false;
            if (Kind != RaidKind.Anton) return false;
            var key = (identity.InstanceId, identity.CharacterId);
            var entry = new DungeonBinding(identity, current.Phase, dungeon);
            if (entries.TryGetValue(key, out var previous)) return previous == entry;
            if (entries.Keys.Any(old => old.Run == identity.InstanceId)) return false;
            foreach (var old in entries.Where(e => e.Value.Identity.CharacterId == identity.CharacterId).Select(e => e.Key).ToArray())
                entries.Remove(old);
            entries.Add(key, entry);
            return true;
        }
    }

    public AntonRaidBattleChange? CompleteDungeon(DungeonClearFact fact, int? week = null)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Anton || current.State != AntonRaidBattleState.Fighting ||
                fact.ConfirmedDeathEvents.Count == 0 || fact.ClearMap is null ||
                !entries.TryGetValue((fact.Identity.InstanceId, fact.Identity.CharacterId), out var entry) || entry.Run is not null ||
                entry.Identity.CharacterId != fact.Identity.CharacterId || entry.Identity.QuestId != fact.Identity.QuestId ||
                entry.Identity.TaskAttempt != fact.Identity.TaskAttempt || fact.Identity.Epoch < entry.Identity.Epoch ||
                entry.Phase != current.Phase || entry.Dungeon != fact.DungeonId) return null;
            int dungeon = checked((int)fact.DungeonId);
            if (current.Dungeons[dungeon] != RaidDungeonState.Open) return null;
            var before = current;
            // 清图只改变本次正式副本状态；HpRaw 的单位未闭合，不从胜利擅自扣除符号值。
            var after = Evaluate(before with { Dungeons = before.Dungeons.SetItem(dungeon, RaidDungeonState.Clear) }, dungeon, null, null);
            if (after.State == AntonRaidBattleState.Reward) rewardWeek ??= week;
            entries.Remove((fact.Identity.InstanceId, fact.Identity.CharacterId));
            CommitBattleCandidate(before, after);
            return new(before, current);
        }
    }

    // 正式进图预留的 Run 带 entry.Run，旧 CompleteDungeon 故意不消费它。开关打开后才按同一规则迁移战况。
    // HP写入只沿资源SET，计时由Runtime拥有的有界时钟按本服秒制合同消费。
    internal AntonRaidBattleChange? CompleteAntonReservedDungeon(DungeonClearFact fact, int? week = null)
    {
        lock (gate)
        {
            if (!antonPhaseProgress || Kind != RaidKind.Anton || current.State != AntonRaidBattleState.Fighting ||
                fact.ConfirmedDeathEvents.Count == 0 || fact.ClearMap is null ||
                !entries.TryGetValue((fact.Identity.InstanceId, fact.Identity.CharacterId), out var entry) ||
                entry.Run is not { Installed: true } run || run.Cleared ||
                entry.Identity.CharacterId != fact.Identity.CharacterId || entry.Identity.QuestId != fact.Identity.QuestId ||
                entry.Identity.TaskAttempt != fact.Identity.TaskAttempt || fact.Identity.Epoch < entry.Identity.Epoch ||
                entry.Phase != current.Phase || entry.Dungeon != fact.DungeonId ||
                run.Phase != current.Phase || run.Dungeon != fact.DungeonId ||
                HasAntonRewards && run.Generation != current.DungeonGenerations.GetValueOrDefault(checked((int)run.Dungeon))) return null;
            var owner = run.Actors.FirstOrDefault(row => row.Identity == entry.Identity);
            if (owner.Actor is not { IsCurrent: true } actor) return null;
            int dungeon = checked((int)fact.DungeonId);
            if (!current.Dungeons.TryGetValue(dungeon, out var state) || state != RaidDungeonState.Open) return null;
            var before = current;
            var after = Evaluate(before with { Dungeons = before.Dungeons.SetItem(dungeon, RaidDungeonState.Clear) }, dungeon, null, null);
            run.ClearFacts.Add(actor, fact);
            if (after.State == AntonRaidBattleState.Reward) rewardWeek ??= week;
            CommitBattleCandidate(before, after);
            return new(before, current);
        }
    }

    internal bool AllowsAntonEntry(int dungeon)
    {
        lock (gate) return AntonEntryOpenLocked(dungeon);
    }

    bool AntonEntryOpenLocked(int dungeon)
    {
        if (Kind != RaidKind.Anton || pendingTransition is not null || current.State != AntonRaidBattleState.Fighting ||
            !current.Dungeons.TryGetValue(dungeon, out var state) || state != RaidDungeonState.Open) return false;
        if (!antonPhaseProgress) return current.Phase == 0 && dungeon == 210;
        return script.Phases[current.Phase].Dungeons.Any(row => row.Id == dungeon);
    }

    // 机制值只能来自服务端已验证的机制；网络 RAID_SET_SYMBOL 不调用这个入口。
    public AntonRaidBattleChange? SetMechanism(Guid instance, long revision, string symbol, int value)
    {
        lock (gate)
        {
            if (instance != Instance || revision != current.Revision || pendingTransition is not null || TerminalRequested(current) || LukePhaseDueLocked() ||
                !current.Symbols.TryGetValue(symbol, out int previous) || previous == value) return null;
            var before = current;
            CommitBattleCandidate(before, Evaluate(StampLukeFacilityWrite(before with { Symbols = before.Symbols.SetItem(symbol, value) }, symbol), null, symbol, null));
            return new(before, current);
        }
    }

    // 安徒恩 SET TIMER / PHASE TIME OVER 的原值单位未闭合，拒绝把声明数字当成到期事实。
    // 卢克仍走 ExpireLukeTimer。其它调用方必须自备已证单位，不能靠本入口猜测秒或毫秒。
    public AntonRaidBattleChange? ExpireTimer(Guid instance, long revision, RaidTimer timer)
    {
        lock (gate)
        {
            if (Kind == RaidKind.Anton || instance != Instance || revision != current.Revision || pendingTransition is not null || TerminalRequested(current) || LukePhaseDueLocked() ||
                !current.Timers.ContainsKey(timer)) return null;
            var before = current;
            CommitBattleCandidate(before, Evaluate(before with { Timers = before.Timers.Remove(timer), TimerAnchors = before.TimerAnchors.Remove(timer), TimerGenerations = before.TimerGenerations.Remove(timer) }, null, null, timer));
            return new(before, current);
        }
    }

    // 计时任务持有SET时签发的代；其它图推进revision后仍可到期，但被RESET/重设的旧任务无权触发。
    public AntonRaidBattleChange? ExpireLukeTimer(Guid instance, RaidTimer timer, long generation)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Luke || instance != Instance || pendingTransition is not null ||
                TerminalRequested(current) || LukePhaseDueLocked() || generation <= 0 ||
                current.TimerGenerations.GetValueOrDefault(timer) != generation || !current.Timers.ContainsKey(timer)) return null;
            var before = current;
            CommitBattleCandidate(before, Evaluate(before with { Timers = before.Timers.Remove(timer),
                TimerGenerations = before.TimerGenerations.Remove(timer), TimerAnchors = before.TimerAnchors.Remove(timer) }, null, null, timer));
            return new(before, current);
        }
    }

    public AntonRaidBattleChange? AdvanceAfterRewards(Guid instance, long revision)
    {
        lock (gate)
        {
            if (instance != Instance || revision != current.Revision || pendingTransition is not null || current.ClockFaulted || current.State != AntonRaidBattleState.Reward ||
                members.Any(member => member.Value.Length != PhaseCount) ||
                Kind == RaidKind.Luke && (dungeonRuns.Count != 0 || lukeRevivalReservations.Count != 0) ||
                !AllRewardsConfirmed(current.Phase)) return null;
            var before = current;
            current = before.Phase + 1 == script.Phases.Length
                ? before with { State = AntonRaidBattleState.Completed, Revision = before.Revision + 1, Timers = ImmutableDictionary<RaidTimer, int>.Empty,
                    TimerGenerations = ImmutableDictionary<RaidTimer, long>.Empty, TimerAnchors = ImmutableDictionary<RaidTimer, long>.Empty }
                : Initialize(before.Phase + 1, before.Revision + 1, Kind == RaidKind.Luke ? before.Symbols : null, before.TimerSequence);
            // 旧阶段实例不能在下一阶段再次产生clear，集合随阶段释放，不随游玩时长增长。
            entries.Clear();
            AnchorLukePhaseLocked();
            ScheduleLukeClockLocked();
            return new(before, current);
        }
    }

    public AntonRaidBattleChange? CompleteDeferred(Guid instance, long revision, int token, int? selectedDungeon = null)
    {
        lock (gate)
        {
            if (instance != Instance || revision != current.Revision || pendingTransition is not null || TerminalRequested(current) || LukePhaseDueLocked() ||
                !current.Deferred.TryGetValue(token, out var action)) return null;
            int id;
            RaidDungeonState next;
            if (action.Tag == "[reserve dungeon state]")
            { id = action.DungeonId!.Value; next = action.State!.Value; }
            else if (action.Tag == "[open random hatchery]")
            {
                // 候选集合属于机制规则；调用方必须给出已验证目标，本层不凭ID范围猜孵化场。
                if (selectedDungeon is not { } selected || !current.Dungeons.ContainsKey(selected)) return null;
                id = selected; next = RaidDungeonState.Open;
            }
            else return null;
            var before = current;
            CommitBattleCandidate(before, Evaluate(before with { Deferred = before.Deferred.Remove(token),
                DeferredAnchors = before.DeferredAnchors.Remove(token), DeferredGenerations = before.DeferredGenerations.Remove(token),
                DungeonGenerations = Kind == RaidKind.Anton && HasAntonRewards && next == RaidDungeonState.Open && before.Dungeons[id] != next
                    ? before.DungeonGenerations.SetItem(id, checked(before.DungeonGenerations.GetValueOrDefault(id) + 1)) : before.DungeonGenerations,
                Dungeons = before.Dungeons.SetItem(id, next) }, id, null, null));
            return new(before, current);
        }
    }

    static bool TerminalRequested(AntonRaidBattleView view) => view.PhaseExpired || view.ClockFaulted || view.State != AntonRaidBattleState.Fighting ||
        view.Deferred.Values.Any(action => action.Tag == "[fail phase]");

    AntonRaidBattleView Evaluate(AntonRaidBattleView state, int? dungeon, string? symbol, RaidTimer? timer, RaidStageFact? fact = null)
    {
        var events = new Queue<(int? Dungeon, string? Symbol, RaidTimer? Timer, RaidStageFact? Fact)>();
        events.Enqueue((dungeon, symbol, timer, fact));
        int budget = 4096;
        while (!TerminalRequested(state) && events.TryDequeue(out var changed))
        {
            if (--budget == 0) throw new InvalidDataException("攻坚规则循环超出单次事件预算");
            foreach (var rule in script.Phases[state.Phase].Rules)
            {
                // 当前规则的终结尾动作仍执行，之后不再匹配新的旧阶段规则。
                if (TerminalRequested(state)) break;
                if (!rule.Trigger.All(Matches)) continue;
                foreach (var action in rule.Behavior)
                {
                    switch (action.Tag)
                    {
                        case "[set]":
                            string key = action.Symbol!;
                            int value = action.Operator switch
                            {
                                null => action.RawIntegers[0],
                                "[+]" => checked(state.Symbols[key] + action.RawIntegers[0]),
                                _ => throw new InvalidDataException("未支持的攻坚符号运算 " + action.Operator)
                            };
                            state = StampLukeFacilityWrite(state, key);
                            if (state.Symbols[key] != value)
                            { state = state with { Symbols = state.Symbols.SetItem(key, value) }; events.Enqueue((null, key, null, null)); }
                            break;
                        case "[set dungeon state]":
                            int id = action.DungeonId!.Value; var next = action.State!.Value;
                            if (state.Dungeons[id] != next)
                            {
                                // 同Open不续HP；真正重开才开始新目标代，旧Run不能完成新代目标。
                                if (next == RaidDungeonState.Open && (Kind == RaidKind.Luke || HasAntonRewards))
                                {
                                    state = state with { DungeonGenerations = state.DungeonGenerations.SetItem(id,
                                        checked(state.DungeonGenerations.GetValueOrDefault(id) + 1)) };
                                    if (Kind == RaidKind.Luke)
                                    {
                                        var definition = script.Phases[state.Phase].Dungeons.Single(d => d.Id == id);
                                        bool hpChanged = state.Symbols[definition.HpSymbol] != definition.HpRaw;
                                        state = state with { Symbols = state.Symbols.SetItem(definition.HpSymbol, definition.HpRaw) };
                                        if (hpChanged) events.Enqueue((null, definition.HpSymbol, null, null));
                                    }
                                }
                                state = state with { Dungeons = state.Dungeons.SetItem(id, next) };
                                events.Enqueue((id, null, null, null));
                            }
                            break;
                        case "[set timer]":
                            long generation = checked(state.TimerSequence + 1);
                            state = state with { Timers = state.Timers.SetItem(action.Timer!, action.RawIntegers[2]),
                                TimerGenerations = state.TimerGenerations.SetItem(action.Timer!, generation),
                                TimerAnchors = state.TimerAnchors.SetItem(action.Timer!, lukeClock.GetTimestamp()), TimerSequence = generation };
                            break;
                        case "[reset timer]":
                            state = state with { Timers = state.Timers.Remove(action.Timer!), TimerAnchors = state.TimerAnchors.Remove(action.Timer!), TimerGenerations = state.TimerGenerations.Remove(action.Timer!) };
                            break;
                        case "[clear phase]":
                            state = state with { State = AntonRaidBattleState.Reward, Timers = ImmutableDictionary<RaidTimer, int>.Empty,
                                TimerGenerations = ImmutableDictionary<RaidTimer, long>.Empty, TimerAnchors = ImmutableDictionary<RaidTimer, long>.Empty,
                                Deferred = Kind == RaidKind.Anton ? ImmutableDictionary<int, RaidClause>.Empty : state.Deferred,
                                DeferredAnchors = ImmutableDictionary<int, long>.Empty, DeferredGenerations = ImmutableDictionary<int, long>.Empty };
                            break;
                        // 调度与随机目标未证实时保留待执行事实；不能静默丢弃或改为立即开图。
                        case "[reserve dungeon state]":
                        case "[open random hatchery]":
                        case "[reset sanctum object]" when Kind == RaidKind.Luke:
                            state = state with { Deferred = state.Deferred.SetItem(action.TokenIndex, action),
                                DeferredAnchors = state.DeferredAnchors.SetItem(action.TokenIndex, lukeClock.GetTimestamp()),
                                DeferredGenerations = state.DeferredGenerations.SetItem(action.TokenIndex, checked(state.TimerSequence + 1)),
                                TimerSequence = checked(state.TimerSequence + 1) };
                            break;
                        case "[fail phase]" when Kind == RaidKind.Luke:
                            state = state with { Deferred = state.Deferred.SetItem(action.TokenIndex, action),
                                Timers = ImmutableDictionary<RaidTimer, int>.Empty,
                                TimerGenerations = ImmutableDictionary<RaidTimer, long>.Empty, TimerAnchors = ImmutableDictionary<RaidTimer, long>.Empty };
                            break;
                        default: throw new InvalidDataException("未支持的攻坚行为 " + action.Tag);
                    }
                }
            }

            bool Matches(RaidClause clause) => clause.Tag switch
            {
                "[if]" => state.Symbols[clause.Symbol!] == clause.RawIntegers[0],
                "[on change symbol]" => changed.Symbol == clause.Symbol,
                "[on change dungeon state]" => changed.Dungeon == clause.DungeonId,
                "[check dungeon state]" => state.Dungeons[clause.DungeonId!.Value] == clause.State,
                "[check timer end]" => changed.Timer == clause.Timer,
                "[on enter dungeon]" when Kind == RaidKind.Luke => changed.Fact is { Kind: RaidStageFactKind.EnterDungeon } entered &&
                    entered.Phase == state.Phase && entered.Dungeon == clause.DungeonId,
                "[on enter map]" when Kind == RaidKind.Luke => changed.Fact is { Kind: RaidStageFactKind.EnterMap } map &&
                    map.Phase == state.Phase && map.Map == clause.MapId,
                "[on giveup dungeon]" when Kind == RaidKind.Luke => changed.Fact is { Kind: RaidStageFactKind.GiveUpDungeon } abandoned &&
                    abandoned.Phase == state.Phase && abandoned.Dungeon == clause.DungeonId,
                "[phase end]" when Kind == RaidKind.Luke => changed.Fact is { Kind: RaidStageFactKind.PhaseEnd } ended &&
                    ended.Phase == state.Phase && clause.RawIntegers.Length == 1 && clause.RawIntegers[0] == ended.Phase,
                _ => throw new InvalidDataException("未支持的攻坚触发器 " + clause.Tag)
            };
        }
        return state;
    }
}
