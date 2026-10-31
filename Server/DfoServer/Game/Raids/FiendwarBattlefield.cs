using System.Collections.Immutable;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 同一政策用于全部人数；总时限与罗什输出窗口不在放缓倍率中。
internal sealed record FiendwarPacing(int EnemySpawn = 2, int EnemyMove = 2, int FacilityRestore = 2,
    int DungeonLimit = 2, int MerchantStay = 2)
{
    public int TimerSeconds(string key, int seconds) => checked(seconds * (key switch
    {
        "top_create_enemy" or "mid_create_enemy" or "bot_create_enemy" => EnemySpawn,
        "move_enemy" or "warning_enemy" => EnemyMove,
        "top_restore_center" or "mid_restore_center" or "bot_restore_center" => FacilityRestore,
        _ => 1
    }));
    public bool Valid => EnemySpawn is >= 1 and <= 10 && EnemyMove is >= 1 and <= 10 &&
        FacilityRestore is >= 1 and <= 10 && DungeonLimit is >= 1 and <= 10 && MerchantStay is >= 1 and <= 10;
}

internal readonly record struct FiendwarUnit(long Serial, int Kind);
internal sealed record FiendwarAreaState(FiendwarUnit? Enemy = null, FiendwarUnit? Monster = null,
    bool Occupied = false, bool Warning = false)
{
    public string State => Occupied ? Enemy is not null ? "user_enemy" : Monster is not null ? "user_monster" : "empty" :
        Enemy is not null ? Monster is not null ? "enemy_monster" : "enemy" : Monster is not null ? "monster" : "empty";
}
internal readonly record struct FiendwarTimerKey(string Kind, int Area);
internal sealed record FiendwarTimer(DateTimeOffset Started, DateTimeOffset End);
internal sealed record FiendwarMerchant(int Area, int Kind, int X, int Y, DateTimeOffset End);
internal sealed record FiendwarFieldState(Guid Instance, long Revision, DateTimeOffset Started, DateTimeOffset Deadline,
    ImmutableDictionary<int, FiendwarAreaState> Areas, ImmutableDictionary<string, int> Symbols,
    ImmutableDictionary<int, string> Dungeons, ImmutableDictionary<FiendwarTimerKey, FiendwarTimer> Timers,
    FiendwarMerchant? Merchant, long NextUnit, ulong RandomState, bool Cleared = false, bool Failed = false)
{
    // 成就要求本次全程未拆设施；恢复、重发和最终状态归零不能抹除既有事实。
    internal bool FacilitiesDestroyed { get; init; }
    // 原生基地危险检查6/7/8区域的敌军容器；区域倒计时Warning不是此判据。
    internal bool BaseThreatened { get; init; }
}
internal sealed record FiendwarFieldEvent(FiendwarOp? Kind = null, int Id = 0, string? Key = null);
internal sealed record FiendwarFieldChange(FiendwarFieldState State, ImmutableArray<FiendwarInstruction> Effects);

// 只计算不可变战场快照。调用方在原run/owner提交边界安装结果，异常不改变已发布状态。
internal sealed class FiendwarBattlefield(FiendwarScript script, FiendwarPacing pacing)
{
    readonly int[] areaIds = script.Areas.Keys.Order().ToArray();

    internal FiendwarFieldState BossSymbols(FiendwarFieldState source, IReadOnlyDictionary<string, int> values, DateTimeOffset now)
    {
        if (values.Keys.Any(key => key is not ("[top boss state]" or "[bot boss state]" or "[boss pattern state]" or
            "[top boss hp]" or "[bot boss hp]" or "[top boss view hp]" or "[bot boss view hp]")))
            throw new ArgumentException("非罗什符号", nameof(values));
        return Apply(source, new(), now, values.Select(row => new FiendwarInstruction(FiendwarOp.SetSymbol, row.Value, Key: row.Key)).ToImmutableArray()).State;
    }

    public FiendwarFieldChange Create(Guid instance, DateTimeOffset now, ulong seed, int guideRoute = 0)
    {
        if (instance == Guid.Empty || !script.Script.Issues.IsEmpty || !pacing.Valid || guideRoute is < 0 or > 1 || !script.Guide && guideRoute != 0)
            throw new InvalidOperationException("漩涡战场定义不可执行");
        var state = new FiendwarFieldState(instance, 0, now, now.AddSeconds(script.TotalSeconds),
            script.Areas.ToImmutableDictionary(row => row.Key, _ => new FiendwarAreaState()),
            script.Script.Symbols.Keys.ToImmutableDictionary(key => key, _ => 0, StringComparer.Ordinal),
            script.Script.Phases[0].Dungeons.ToImmutableDictionary(row => row.Id, row => row.InitialState switch
            { RaidDungeonState.Hide => "hide", RaidDungeonState.Close => "close", _ => "open" }),
            ImmutableDictionary<FiendwarTimerKey, FiendwarTimer>.Empty, null, 0, seed == 0 ? 1 : seed);
        if (script.Guide)
        {
            state = state with { Symbols = state.Symbols.SetItem("[single raid type]", guideRoute) };
            return Apply(state, new(FiendwarOp.SymbolChanged, Key: "[single raid type]"), now);
        }
        return Apply(state, new(), now);
    }

    public FiendwarFieldChange Tick(FiendwarFieldState source, DateTimeOffset now)
    {
        if (source.Cleared || source.Failed) return new(source, []);
        if (now >= source.Deadline) return new(source with { Failed = true, Revision = source.Revision + 1,
            Timers = source.Timers.Clear(), Merchant = null }, []);
        var current = source;
        var effects = ImmutableArray.CreateBuilder<FiendwarInstruction>();
        // 依截止时间顺序推进，避免一次延迟tick把旧兵线timer覆盖掉新timer。
        int budget = 2048;
        while (current.Timers.Where(row => row.Value.End <= now).OrderBy(row => row.Value.End)
            .ThenBy(row => row.Key.Kind, StringComparer.Ordinal).ThenBy(row => row.Key.Area).Take(1).ToArray() is [var expired])
        {
            if (--budget == 0) throw new InvalidOperationException("漩涡计时推进超过预算");
            current = current with { Timers = current.Timers.Remove(expired.Key) };
            var change = Apply(current, new(FiendwarOp.TimerEnded, expired.Key.Area, expired.Key.Kind), expired.Value.End);
            current = change.State;
            effects.AddRange(change.Effects);
            if (current.Cleared || current.Failed) break;
        }
        if (current.Merchant is { } merchant && merchant.End <= now)
        {
            // 商人随机选择属于本次快照，重发不重抽。
            current = current with { Merchant = null };
            var change = Apply(current, new(), now, [new(FiendwarOp.CreateMerchant)]);
            current = change.State;
            effects.AddRange(change.Effects);
        }
        if (!ReferenceEquals(current, source)) current = current with { Revision = source.Revision + 1 };
        return new(current, effects.ToImmutable());
    }

    public FiendwarFieldChange Occupy(FiendwarFieldState source, int area, bool occupied, DateTimeOffset now)
    {
        if (!source.Areas.TryGetValue(area, out var old)) throw new ArgumentOutOfRangeException(nameof(area));
        if (old.Occupied == occupied || source.Cleared || source.Failed) return new(source, []);
        return Apply(source, new(), now, areaUpdate: (area, old with { Occupied = occupied }));
    }

    public FiendwarFieldChange SetSymbol(FiendwarFieldState source, string key, int value, DateTimeOffset now) =>
        source.Symbols.ContainsKey(key) && value >= 0 ? Apply(source, new(), now, [new(FiendwarOp.SetSymbol, value, Key: key)]) :
            throw new ArgumentOutOfRangeException(nameof(key));

    public FiendwarFieldChange CompleteArea(FiendwarFieldState source, int area, DateTimeOffset now)
    {
        if (!script.Areas.TryGetValue(area, out var definition) || definition.DungeonId <= 0)
            throw new ArgumentOutOfRangeException(nameof(area));
        var actions = ImmutableArray.CreateBuilder<FiendwarInstruction>();
        foreach (string role in definition.Roles)
        {
            string symbol = role.Replace(" area]", " state]", StringComparison.Ordinal);
            // 黑白本体完成由双世界汇合验证后提供，不能把单图clear当全团胜利。
            if (symbol is "[top boss state]" or "[bot boss state]") continue;
            if (source.Symbols.ContainsKey(symbol)) actions.Add(new(FiendwarOp.SetSymbol,
                symbol is "[radar state]" or "[battery state]" ? 1 : 0, Key: symbol));
        }
        actions.Add(new(FiendwarOp.SetDungeon, definition.DungeonId, Key: "clear"));
        return Apply(source, new(), now, actions.ToImmutable(), (area, new()));
    }

    public FiendwarFieldChange Apply(FiendwarFieldState source, FiendwarFieldEvent signal, DateTimeOffset now) =>
        Apply(source, signal, now, []);

    FiendwarFieldChange Apply(FiendwarFieldState source, FiendwarFieldEvent signal, DateTimeOffset now,
        ImmutableArray<FiendwarInstruction> actions = default, (int Area, FiendwarAreaState State)? areaUpdate = null)
    {
        if (source.Cleared || source.Failed) return new(source, []);
        if (now < source.Started) throw new ArgumentOutOfRangeException(nameof(now));
        if (now >= source.Deadline) return new(source with { Failed = true, Revision = source.Revision + 1,
            Timers = source.Timers.Clear(), Merchant = null }, []);
        var state = source;
        var pending = new Queue<FiendwarFieldEvent>();
        var effects = ImmutableArray.CreateBuilder<FiendwarInstruction>();
        pending.Enqueue(signal);
        if (areaUpdate is { } update) Area(update.Area, update.State);
        if (!actions.IsDefault) foreach (var action in actions) Act(action);
        int budget = 2048;
        while (pending.TryDequeue(out var current) && !state.Failed && !state.Cleared)
        {
            if (--budget == 0) throw new InvalidOperationException("漩涡规则推进超过预算");
            foreach (var rule in script.Rules)
            {
                if (!rule.Conditions.All(condition => Matches(condition, current))) continue;
                foreach (var action in rule.Actions) Act(action);
                if (state.Failed || state.Cleared) break;
            }
        }
        if (!ReferenceEquals(state, source) || effects.Count != 0) state = state with { Revision = source.Revision + 1 };
        if (state.Cleared || state.Failed) state = state with { Timers = state.Timers.Clear(), Merchant = null };
        return new(state, effects.ToImmutable());

        bool Matches(FiendwarInstruction c, FiendwarFieldEvent e) => c.Op switch
        {
            FiendwarOp.SymbolEquals => state.Symbols[c.Key!] == c.A,
            FiendwarOp.SymbolChanged => e.Kind == c.Op && e.Key == c.Key,
            FiendwarOp.AreaEquals => state.Areas[c.A].State == c.Key,
            FiendwarOp.DungeonEquals => state.Dungeons.GetValueOrDefault(c.A) == c.Key,
            FiendwarOp.AliveMonstersLess => state.Areas.Values.Count(a => a.Monster is not null) < c.A,
            FiendwarOp.TimerEnded => e.Kind == c.Op && e.Id == c.A && e.Key == c.Key,
            FiendwarOp.AreaChanged or FiendwarOp.DungeonChanged or FiendwarOp.EnterDungeon or FiendwarOp.EnterMap or
                FiendwarOp.GiveupArea or FiendwarOp.GiveupDungeon => e.Kind == c.Op && e.Id == c.A,
            _ => throw new InvalidOperationException("漩涡条件未实现：" + c.Op)
        };
        int Draw(int exclusive)
        {
            if (exclusive <= 0) throw new InvalidOperationException("漩涡随机池为空");
            ulong next = state.RandomState;
            next ^= next >> 12; next ^= next << 25; next ^= next >> 27;
            state = state with { RandomState = next };
            return (int)(unchecked(next * 2685821657736338717UL) % (uint)exclusive);
        }
        int Weighted(int[] weights)
        {
            int roll = Draw(checked(weights.Sum()));
            for (int i = 0; i < weights.Length; i++) if ((roll -= weights[i]) < 0) return i;
            throw new InvalidOperationException("漩涡随机权重非法");
        }
        void Symbol(string key, int value)
        {
            if (value == 1 && key is "[radar state]" or "[battery state]")
                state = state with { FacilitiesDestroyed = true };
            if (state.Symbols.GetValueOrDefault("[movie command center on]") == 1 && value == 1 &&
                key is "[top command center state]" or "[mid command center state]" or "[bot command center state]") return;
            if (state.Symbols.GetValueOrDefault(key) == value) return;
            state = state with { Symbols = state.Symbols.SetItem(key, value) };
            if (key == "[movie command center on]" && value == 1)
                state = state with { Timers = state.Timers.RemoveRange(state.Timers.Keys.Where(k => k.Kind.EndsWith("_restore_center", StringComparison.Ordinal))) };
            pending.Enqueue(new(FiendwarOp.SymbolChanged, Key: key));
        }
        void Area(int id, FiendwarAreaState value)
        {
            var old = state.Areas[id];
            if (old == value) return;
            state = state with { Areas = state.Areas.SetItem(id, value),
                BaseThreatened = state.BaseThreatened || id is 6 or 7 or 8 && value.Enemy is not null };
            if (old.State != value.State)
            {
                string symbol = $"[{id} area state]";
                int number = value.State switch { "enemy" => 1, "monster" => 2, "user_enemy" => 3, "user_monster" => 4, "enemy_monster" => 5, _ => 0 };
                if (state.Symbols.ContainsKey(symbol)) Symbol(symbol, number);
                pending.Enqueue(new(FiendwarOp.AreaChanged, id));
            }
        }
        void Act(FiendwarInstruction a)
        {
            switch (a.Op)
            {
                case FiendwarOp.SetSymbol: Symbol(a.Key!, a.A); break;
                case FiendwarOp.SetTimer:
                    if (state.Symbols.GetValueOrDefault("[movie command center on]") == 1 &&
                        a.Key!.EndsWith("_restore_center", StringComparison.Ordinal)) break;
                    int seconds = pacing.TimerSeconds(a.Key!, a.B);
                    if (seconds <= 0) throw new InvalidOperationException("漩涡timer必须为正数");
                    state = state with { Timers = state.Timers.SetItem(new(a.Key!, a.A), new(now, now.AddSeconds(seconds))) };
                    break;
                case FiendwarOp.ResetTimer: state = state with { Timers = state.Timers.Remove(new(a.Key!, a.A)) }; break;
                case FiendwarOp.SetDungeon:
                    if (state.Dungeons.GetValueOrDefault(a.A) != a.Key)
                    {
                        state = state with { Dungeons = state.Dungeons.SetItem(a.A, a.Key!) };
                        pending.Enqueue(new(FiendwarOp.DungeonChanged, a.A));
                    }
                    break;
                case FiendwarOp.CreateEnemy:
                    if (state.Areas[a.A].Enemy is not null) break;
                    var kind = script.EnemyRates[Weighted(script.EnemyRates.Select(r => r.Weight).ToArray())].Kind;
                    state = state with { NextUnit = state.NextUnit + 1 };
                    Area(a.A, state.Areas[a.A] with { Enemy = new(state.NextUnit, kind) });
                    break;
                case FiendwarOp.CreateSingleEnemy:
                case FiendwarOp.CreateSingleMonster:
                    if (!script.Guide || a.B <= 0) throw new InvalidOperationException("非原版引导固定遭遇");
                    state = state with { NextUnit = state.NextUnit + 1 };
                    var single = new FiendwarUnit(state.NextUnit, a.B);
                    Area(a.A, a.Op == FiendwarOp.CreateSingleEnemy ? state.Areas[a.A] with { Enemy = single } :
                        state.Areas[a.A] with { Monster = single });
                    int dungeon = script.Areas[a.A].DungeonId;
                    if (dungeon > 0) Act(new(FiendwarOp.SetDungeon, dungeon, Key: "open"));
                    break;
                case FiendwarOp.MoveEnemy:
                    var sourceArea = state.Areas[a.A]; var targetArea = state.Areas[a.B];
                    if (sourceArea.Enemy is null || sourceArea.Occupied) break;
                    // 前方已有军团时保留来源，不能把两支合成一支而悄悄吞掉敌军。
                    if (targetArea.Enemy is not null)
                    {
                        state = state with { Timers = state.Timers.SetItem(new("move_enemy", a.A), new(now, now.AddSeconds(5))) };
                        break;
                    }
                    Area(a.A, sourceArea with { Enemy = null, Warning = false });
                    Area(a.B, targetArea with { Enemy = sourceArea.Enemy, Warning = false });
                    state = state with { Timers = state.Timers.Remove(new("move_enemy", a.A)).Remove(new("warning_enemy", a.A)) };
                    break;
                case FiendwarOp.DeleteMonster: Area(a.A, state.Areas[a.A] with { Monster = null }); break;
                case FiendwarOp.WarningEnemy: Area(a.A, state.Areas[a.A] with { Warning = true }); break;
                case FiendwarOp.DeleteAllEnemy:
                    foreach (int id in areaIds) Area(id, state.Areas[id] with { Enemy = null, Warning = false });
                    state = state with { Timers = state.Timers.RemoveRange(state.Timers.Keys.Where(k => k.Kind.Contains("enemy", StringComparison.Ordinal))) };
                    break;
                case FiendwarOp.CreateMonsters:
                    var available = areaIds.Where(id => script.Areas[id].DungeonId > 0 && script.Areas[id].Roles.IsEmpty &&
                        state.Areas[id] is { Enemy: null, Monster: null, Occupied: false }).ToList();
                    int count = state.Areas.Values.Count(v => v.Monster is not null);
                    while (count++ < 3 && available.Count > 0)
                    {
                        int pick = Draw(available.Count), id = available[pick]; available.RemoveAt(pick);
                        int monster = Draw(2) + 1;
                        state = state with { NextUnit = state.NextUnit + 1 };
                        Area(id, state.Areas[id] with { Monster = new(state.NextUnit, monster) });
                    }
                    break;
                case FiendwarOp.CreateMerchant:
                    var position = script.MerchantPositions[Draw(script.MerchantPositions.Length)];
                    var merchant = script.MerchantRates[Weighted(script.MerchantRates.Select(r => r.Weight).ToArray())];
                    state = state with { Merchant = new(position.Area, merchant.Kind, position.X, position.Y,
                        now.AddSeconds(checked(merchant.Seconds * pacing.MerchantStay))) };
                    break;
                case FiendwarOp.Clear: state = state with { Cleared = true }; break;
                case FiendwarOp.Fail: state = state with { Failed = true }; break;
                case FiendwarOp.BossPatternInit or FiendwarOp.MovieRadar or FiendwarOp.MovieBatteryOn or
                    FiendwarOp.MovieBatteryOff or FiendwarOp.MovieCommandCenter or FiendwarOp.KillAllCharacters:
                    effects.Add(a); break;
                default: throw new InvalidOperationException("漩涡动作未实现：" + a.Op);
            }
        }
    }
}
