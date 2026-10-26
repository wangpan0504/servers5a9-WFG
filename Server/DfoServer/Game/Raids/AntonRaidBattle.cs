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
}
internal sealed record AntonRaidBattleChange(AntonRaidBattleView Before, AntonRaidBattleView After);

// 正式实例的阶段聚合。输入只有已绑定副本的清图事实、服务端计时器和服务端机制结果；不执行客户端 SET。
internal sealed class AntonRaidBattle
{
    readonly object gate = new();
    readonly RaidScript script;
    readonly Dictionary<Guid, (DungeonRunIdentity Identity, int Phase, uint Dungeon)> entries = [];
    readonly Dictionary<RaidActor, ImmutableArray<RaidPhaseRewardPolicy>> members = [];
    readonly HashSet<(ushort Character, int Phase, byte Kind)> rewards = [];
    AntonRaidBattleView current;
    int? rewardWeek;
    public Guid Instance { get; }
    public int PhaseCount => script.Phases.Length;
    public int? RewardWeek { get { lock (gate) return rewardWeek; } }
    public AntonRaidBattleView View { get { lock (gate) return current; } }

    public bool Join(RaidActor actor, ImmutableArray<RaidPhaseRewardPolicy> policy)
    {
        lock (gate)
        {
            if (!actor.IsCurrent || current.Revision != 0 || members.Count >= 20 || policy.Length != PhaseCount ||
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

    public RaidPhaseRewardPolicy? RewardPolicy(RaidActor actor, int phase)
    { lock (gate) return actor.IsCurrent && members.TryGetValue(actor, out var policy) && phase >= 0 && phase < policy.Length ? policy[phase] : null; }

    public bool ConfirmReward(RaidActor actor, PreparedRaidReward prepared)
    {
        lock (gate)
        {
            if (!actor.IsCurrent || prepared.Instance != Instance || prepared.Character != actor.Character ||
                prepared.Phase != current.Phase || current.State != AntonRaidBattleState.Reward || !members.ContainsKey(actor)) return false;
            rewards.Add((actor.Character, current.Phase, prepared.Kind));
            return members.All(m => m.Value[current.Phase].Offers.Select(o => o.Kind).Distinct()
                .All(kind => rewards.Contains((m.Key.Character, current.Phase, kind))));
        }
    }

    public void Publish(AntonRaidBattleView view)
    {
        RaidActor[] recipients;
        lock (gate)
        {
            if (!ReferenceEquals(current, view)) return;
            recipients = members.Keys.ToArray();
        }
        foreach (var actor in recipients)
            if (actor.IsCurrent) actor.Session.RaidBattleNoticeArrived?.Invoke(view, actor);
    }

    public bool IsCurrent(AntonRaidBattleView view) { lock (gate) return ReferenceEquals(current, view); }

    public AntonRaidBattle(RaidScript script, Guid? instance = null)
    {
        if (!script.Issues.IsEmpty || script.Phases.IsEmpty || script.Phases.Length > sbyte.MaxValue ||
            script.Symbols.Count > byte.MaxValue || script.Phases.Any(p => p.Dungeons.Length > byte.MaxValue))
            throw new InvalidDataException("攻坚阶段资源不完整或超出已证协议范围");
        this.script = script;
        Instance = instance ?? Guid.NewGuid();
        if (Instance == Guid.Empty) throw new InvalidDataException("攻坚实例身份不能为空");
        current = Initialize(0, 0);
    }

    AntonRaidBattleView Initialize(int phase, long revision)
    {
        var definition = script.Phases[phase];
        var symbols = script.Symbols.Keys.ToImmutableDictionary(s => s, _ => 0);
        foreach (var dungeon in definition.Dungeons) symbols = symbols.SetItem(dungeon.HpSymbol, dungeon.HpRaw);
        return Evaluate(new(Instance, revision, phase, AntonRaidBattleState.Fighting, symbols,
            definition.Dungeons.ToImmutableDictionary(d => d.Id, d => d.InitialState),
            ImmutableDictionary<RaidTimer, int>.Empty), null, null, null);
    }

    // 由会话在真正安装正式副本后调用；同一实例不能跨角色、阶段或副本重绑。
    public bool BindDungeon(DungeonRunIdentity identity, uint dungeon)
    {
        lock (gate)
        {
            if (current.State != AntonRaidBattleState.Fighting || !current.Dungeons.TryGetValue(checked((int)dungeon), out var state) ||
                state != RaidDungeonState.Open || identity.InstanceId == Guid.Empty) return false;
            var entry = (identity, current.Phase, dungeon);
            if (entries.TryGetValue(identity.InstanceId, out var previous)) return previous == entry;
            foreach (var old in entries.Where(e => e.Value.Identity.CharacterId == identity.CharacterId).Select(e => e.Key).ToArray())
                entries.Remove(old);
            entries.Add(identity.InstanceId, entry);
            return true;
        }
    }

    public AntonRaidBattleChange? CompleteDungeon(DungeonClearFact fact, int? week = null)
    {
        lock (gate)
        {
            if (current.State != AntonRaidBattleState.Fighting ||
                fact.ConfirmedDeathEvents.Count == 0 || fact.ClearMap is null ||
                !entries.TryGetValue(fact.Identity.InstanceId, out var entry) ||
                entry.Identity.CharacterId != fact.Identity.CharacterId || entry.Identity.QuestId != fact.Identity.QuestId ||
                entry.Identity.TaskAttempt != fact.Identity.TaskAttempt || fact.Identity.Epoch < entry.Identity.Epoch ||
                entry.Phase != current.Phase || entry.Dungeon != fact.DungeonId) return null;
            int dungeon = checked((int)fact.DungeonId);
            if (current.Dungeons[dungeon] != RaidDungeonState.Open) return null;
            var before = current;
            // 清图只改变本次正式副本状态；HpRaw 的单位未闭合，不从胜利擅自扣除符号值。
            var after = Evaluate(before with { Dungeons = before.Dungeons.SetItem(dungeon, RaidDungeonState.Clear) }, dungeon, null, null);
            if (after.State == AntonRaidBattleState.Reward) rewardWeek ??= week;
            entries.Remove(fact.Identity.InstanceId);
            current = after with { Revision = before.Revision + 1 };
            return new(before, current);
        }
    }

    // 机制值只能来自服务端已验证的机制；网络 RAID_SET_SYMBOL 不调用这个入口。
    public AntonRaidBattleChange? SetMechanism(Guid instance, long revision, string symbol, int value)
    {
        lock (gate)
        {
            if (instance != Instance || revision != current.Revision || current.State != AntonRaidBattleState.Fighting ||
                !current.Symbols.TryGetValue(symbol, out int previous) || previous == value) return null;
            var before = current;
            current = Evaluate(before with { Symbols = before.Symbols.SetItem(symbol, value) }, null, symbol, null)
                with { Revision = before.Revision + 1 };
            return new(before, current);
        }
    }

    // 原声明的时长由调度方解释；这里仅消费原阶段、原revision的到期事实，不猜测秒/毫秒。
    public AntonRaidBattleChange? ExpireTimer(Guid instance, long revision, RaidTimer timer)
    {
        lock (gate)
        {
            if (instance != Instance || revision != current.Revision || current.State != AntonRaidBattleState.Fighting ||
                !current.Timers.ContainsKey(timer)) return null;
            var before = current;
            current = Evaluate(before with { Timers = before.Timers.Remove(timer) }, null, null, timer)
                with { Revision = before.Revision + 1 };
            return new(before, current);
        }
    }

    public AntonRaidBattleChange? AdvanceAfterRewards(Guid instance, long revision)
    {
        lock (gate)
        {
            if (instance != Instance || revision != current.Revision || current.State != AntonRaidBattleState.Reward ||
                members.Any(m => m.Value[current.Phase].Offers.Select(o => o.Kind).Distinct()
                    .Any(kind => !rewards.Contains((m.Key.Character, current.Phase, kind))))) return null;
            var before = current;
            current = before.Phase + 1 == script.Phases.Length
                ? before with { State = AntonRaidBattleState.Completed, Revision = before.Revision + 1, Timers = ImmutableDictionary<RaidTimer, int>.Empty }
                : Initialize(before.Phase + 1, before.Revision + 1);
            // 旧阶段实例不能在下一阶段再次产生clear，集合随阶段释放，不随游玩时长增长。
            entries.Clear();
            return new(before, current);
        }
    }

    public AntonRaidBattleChange? CompleteDeferred(Guid instance, long revision, int token, int? selectedDungeon = null)
    {
        lock (gate)
        {
            if (instance != Instance || revision != current.Revision || current.State != AntonRaidBattleState.Fighting ||
                !current.Deferred.TryGetValue(token, out var action)) return null;
            int id;
            RaidDungeonState next;
            if (action.Tag == "[reserve dungeon state]")
            { id = action.DungeonId!.Value; next = action.State!.Value; }
            else
            {
                // 候选集合属于机制规则；调用方必须给出已验证目标，本层不凭ID范围猜孵化场。
                if (selectedDungeon is not { } selected || !current.Dungeons.ContainsKey(selected)) return null;
                id = selected; next = RaidDungeonState.Open;
            }
            var before = current;
            current = Evaluate(before with { Deferred = before.Deferred.Remove(token), Dungeons = before.Dungeons.SetItem(id, next) }, id, null, null)
                with { Revision = before.Revision + 1 };
            return new(before, current);
        }
    }

    AntonRaidBattleView Evaluate(AntonRaidBattleView state, int? dungeon, string? symbol, RaidTimer? timer)
    {
        var events = new Queue<(int? Dungeon, string? Symbol, RaidTimer? Timer)>();
        events.Enqueue((dungeon, symbol, timer));
        int budget = 4096;
        while (state.State == AntonRaidBattleState.Fighting && events.TryDequeue(out var changed))
        {
            if (--budget == 0) throw new InvalidDataException("攻坚规则循环超出单次事件预算");
            foreach (var rule in script.Phases[state.Phase].Rules)
            {
                if (!rule.Trigger.All(Matches)) continue;
                foreach (var action in rule.Behavior)
                {
                    switch (action.Tag)
                    {
                        case "[set]":
                            string key = action.Symbol!; int value = action.RawIntegers[0];
                            if (state.Symbols[key] != value)
                            { state = state with { Symbols = state.Symbols.SetItem(key, value) }; events.Enqueue((null, key, null)); }
                            break;
                        case "[set dungeon state]":
                            int id = action.DungeonId!.Value; var next = action.State!.Value;
                            if (state.Dungeons[id] != next)
                            { state = state with { Dungeons = state.Dungeons.SetItem(id, next) }; events.Enqueue((id, null, null)); }
                            break;
                        case "[set timer]":
                            state = state with { Timers = state.Timers.SetItem(action.Timer!, action.RawIntegers[2]) };
                            break;
                        case "[reset timer]":
                            state = state with { Timers = state.Timers.Remove(action.Timer!) };
                            break;
                        case "[clear phase]":
                            state = state with { State = AntonRaidBattleState.Reward, Timers = ImmutableDictionary<RaidTimer, int>.Empty };
                            break;
                        // 调度与随机目标未证实时保留待执行事实；不能静默丢弃或改为立即开图。
                        case "[reserve dungeon state]":
                        case "[open random hatchery]":
                            state = state with { Deferred = state.Deferred.SetItem(action.TokenIndex, action) };
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
                _ => throw new InvalidDataException("未支持的攻坚触发器 " + clause.Tag)
            };
        }
        return state;
    }
}
