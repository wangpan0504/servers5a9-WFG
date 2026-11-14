using System.Collections.Immutable;
using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static partial class RaidCombatProjection
{
    internal static void PostFiendwarState(GameConnectionContext context, AntonRaidBattleView view, RaidActor actor, Func<bool> current)
    {
        context.Owner.Execute(() =>
        {
            // 完成态不再播放成功电影；资产提交、奖励展示和到期收尾仍沿原事务通知。
            if (view.State == AntonRaidBattleState.Completed) return true;
            var battle = context.Session.CurrentRaidBattle(actor);
            bool SceneReady() => !view.PhaseExpired && view.State != AntonRaidBattleState.Reward ||
                context.Session.Phase == Game.Session.SessionPhase.Town && context.Session.PartyDungeon is null &&
                !context.Sender.DungeonSceneWritten &&
                !context.Session.NeedsFiendwarFinalReturn() &&
                (view.PhaseExpired || context.LukePhaseExit?.ReturnPending(view.Instance) != true);
            if (!SceneReady()) return false;
            var identity = RaidCombatDisplayIdentity.Capture(actor, view);
            bool Show() => current() && SceneReady() && context.RaidDisplay != identity;
            if (view.State == AntonRaidBattleState.Reward && context.Session.RaidCombatResult(view) is { } result)
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_RESULT, RaidCombatPackets.Result(result), "fiendwar_result", Show);
            // 00D30460的3与587的3调用相同胜利窗口；type2必须走1523，不能重装其它团本状态。
            byte state = view.PhaseExpired ? (byte)5 : view.State == AntonRaidBattleState.Reward ? (byte)3 : (byte)2;
            context.Sender.Post(0, (ushort)NotiPacketType.FIEND_WAR_STATE, FiendwarPackets.State(state, 0),
                "fiendwar_state", Show, () => context.RaidDisplay = identity);
            if (view.State == AntonRaidBattleState.Reward && battle is { Kind: RaidKind.Fiendwar })
                _ = PostFiendwarMovieCards(context, actor, battle, view);
            return true;
        });
    }

    internal static void PostFiendwarSnapshot(GameConnectionContext context, AntonRaidBattle battle,
        AntonRaidBattleView view, Func<bool> current)
    {
        // 失败/胜利状态会拆除原生战场；终态后不能继续向旧对象投递单位、计时器和演出事件。
        if (battle.Kind != RaidKind.Fiendwar || view.Fiendwar is not { Failed: false, Cleared: false } field ||
            view.State != AntonRaidBattleState.Fighting || view.PhaseExpired) return;
        bool Guard() => current() && battle.IsCurrent(view);
        if (view.State == AntonRaidBattleState.Fighting && !view.PhaseExpired && !view.ClockFaulted && !field.Failed && !field.Cleared)
        {
            // 00DE9128读取597槽0；type45经01DD1752/01DD187C按同一秒时钟读写，1522本身不设置时间。
            // 入场、重返和战况重放共用真实deadline，排队期间流逝的时间不能重新赠送。
            // 仅按FIFO已写出的秒数去重；同秒新revision不能被稍后会失效的旧排队项挡住。
            // 展示状态只在发送泵的本人owner内读取或更新，不在Battle通知线程读写复合字段。
            uint? writtenRemaining = null;
            context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_REMAIN_TIME, 5,
                () =>
                {
                    var remaining = field.Deadline - battle.FiendwarNow;
                    if (remaining <= TimeSpan.Zero) return null;
                    writtenRemaining = checked((uint)Math.Ceiling(remaining.TotalSeconds));
                    return context.FiendwarRemainingDisplay == (view.Instance, writtenRemaining.Value)
                        ? null : RaidCombatPackets.PhaseRemaining(writtenRemaining.Value);
                }, "fiendwar_remaining", Guard, written: () =>
                {
                    if (writtenRemaining is { } remaining)
                        context.FiendwarRemainingDisplay = (view.Instance, remaining);
                });
        }
        // 差量在FIFO真正写帧时与已写展示比较；旧快照跳过不能吞掉后续必要删除。
        context.Sender.PostPrepared(0, (ushort)NotiPacketType.FIEND_WAR_BOARD_USER_INFO, 1 + 16 * 13,
            () => FiendwarPackets.Users(context.FiendwarUsers.Where(row => view.FiendwarPositions.GetValueOrDefault(row.Key, -1) != row.Value)
                .Select(row => new FiendwarUserRow(2, checked((byte)row.Value), row.Key, 0))
                .Concat(view.FiendwarPositions.OrderBy(row => row.Key)
                    .Select(row => new FiendwarUserRow(1, checked((byte)row.Value), row.Key, 0))).ToArray()),
            "fiendwar_users", Guard, allowShorter: true, written: () => context.FiendwarUsers = view.FiendwarPositions);
        KeyValuePair<int, long>[] warnings = [];
        context.Sender.PostPrepared(0, (ushort)NotiPacketType.FIEND_WAR_BOARD_WARNING, 1 + field.Areas.Count * 2,
            () =>
            {
                // 00D2CB00忽略第二字节；0也重启六秒预警，不能把布尔快照当清除命令。
                warnings = field.WarningEvents.Where(row => field.Areas[row.Key].Warning &&
                    (context.FiendwarEventDisplayInstance != field.Instance ||
                        context.FiendwarWarningDisplays.GetValueOrDefault(row.Key) != row.Value)).OrderBy(row => row.Value).ToArray();
                return warnings.Length == 0 ? null : FiendwarPackets.Warnings(warnings
                    .Select(row => (checked((byte)row.Key), (byte)1)).ToArray());
            }, "fiendwar_warnings", Guard, allowShorter: true, written: () =>
            {
                RecordEventInstance();
                foreach (var row in warnings) context.FiendwarWarningDisplays[row.Key] = row.Value;
            });
        foreach (bool enemy in new[] { false, true })
        {
            // 与同次1530使用同一已提交符号快照；缓存记录实际写出的问号或真实头像。
            var units = FiendwarUnitSnapshot(view, enemy, battle.FiendwarDefinition!.Guide);
            context.Sender.PostPrepared(0, (ushort)(enemy ? NotiPacketType.FIEND_WAR_BOARD_ENEMY_INFO : NotiPacketType.FIEND_WAR_BOARD_MONSTER_INFO),
                1 + 60 * (enemy ? 14 : 10), () =>
                {
                    var old = enemy ? context.FiendwarEnemies : context.FiendwarMonsters;
                    var rows = old.Where(row => !units.TryGetValue(row.Key, out var unit) || unit != row.Value)
                        .Select(row => new FiendwarUnitRow(checked((byte)row.Key), checked((uint)row.Value.Serial), checked((byte)row.Value.Kind), 2))
                        .Concat(units.OrderBy(row => row.Key).Select(row =>
                            new FiendwarUnitRow(checked((byte)row.Key), checked((uint)row.Value.Serial), checked((byte)row.Value.Kind), 1))).ToArray();
                    return enemy ? FiendwarPackets.Enemies(rows) : FiendwarPackets.Monsters(rows);
                }, enemy ? "fiendwar_enemies" : "fiendwar_monsters", Guard, allowShorter: true, written: () =>
                { if (enemy) context.FiendwarEnemies = units; else context.FiendwarMonsters = units; });
        }
        var timers = FiendwarTimerSnapshot(view);
        context.Sender.PostPrepared(0, (ushort)NotiPacketType.FIEND_WAR_BOARD_TIMER_INFO, 1 + byte.MaxValue * 10,
            () => FiendwarPackets.Timers(FiendwarTimerDelta(context.FiendwarTimers, timers)),
            "fiendwar_timers", Guard, allowShorter: true, written: () => context.FiendwarTimers = timers);
        context.Sender.PostPrepared(0, (ushort)NotiPacketType.FIEND_WAR_BOARD_MERCHANT_INFO, 5,
            () => FiendwarPackets.Merchant(checked((byte)(field.Merchant?.Area ?? 0)),
                checked((uint)Math.Max(0, Math.Ceiling((field.Merchant?.End - battle.FiendwarNow)?.TotalSeconds ?? 0)))),
            "fiendwar_merchant", Guard);
        (int Id, int Value)[] symbols = [];
        context.Sender.PostPrepared(0, (ushort)NotiPacketType.FIEND_WAR_SET_SYMBOL,
            1 + battle.SymbolSnapshot(view).Length * 8, () =>
            {
                var snapshot = battle.SymbolSnapshot(view);
                // 状态保留零值；121–125仅按真实事件代发布，off事件的0同样必须送达。
                symbols = snapshot.Where(row => row.Id is < 121 or > 125)
                    .Concat(snapshot.Where(row => row.Id is >= 121 and <= 125 &&
                        field.MovieEvents.TryGetValue(row.Id, out long sequence) &&
                        (context.FiendwarEventDisplayInstance != field.Instance ||
                            context.FiendwarMovieDisplays.GetValueOrDefault(row.Id) != sequence))
                        .OrderBy(row => field.MovieEvents[row.Id])).ToArray();
                return symbols.Length == 0 ? null : FiendwarPackets.Symbols(symbols
                    .Select(row => (checked((uint)row.Id), unchecked((uint)row.Value))).ToArray());
            }, "fiendwar_symbols", Guard, allowShorter: true, written: () =>
            {
                RecordEventInstance();
                foreach (var row in symbols)
                    if (field.MovieEvents.TryGetValue(row.Id, out long sequence)) context.FiendwarMovieDisplays[row.Id] = sequence;
            });
        if (view.FiendwarSupport is { } support)
        {
            var identity = (support.Instance, support.Sequence);
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_SITUATION_NOTICE, FiendwarPackets.SupportNotice(support.Lane),
                "fiendwar_support_notice", () => Guard() && context.FiendwarSupportDisplay != identity &&
                    context.Session.CanPublishFiendwarSupport(support), () => context.FiendwarSupportDisplay = identity);
        }

        void RecordEventInstance()
        {
            if (context.FiendwarEventDisplayInstance == field.Instance) return;
            context.FiendwarMovieDisplays.Clear();
            context.FiendwarWarningDisplays.Clear();
            context.FiendwarEventDisplayInstance = field.Instance;
        }
    }

    internal static ImmutableDictionary<int, FiendwarUnit> FiendwarUnitSnapshot(
        AntonRaidBattleView view, bool enemy, bool guide)
    {
        // 原生两套头像表的0分别是敌军/中立怪问号；引导路线不套团队雷达规则。
        bool reveal = guide || view.Symbols.GetValueOrDefault("[radar state]") == 1;
        return view.Fiendwar!.Areas.Where(row => (enemy ? row.Value.Enemy : row.Value.Monster) is not null)
            .ToImmutableDictionary(row => row.Key, row =>
            {
                var unit = (enemy ? row.Value.Enemy : row.Value.Monster)!.Value;
                return reveal ? unit : unit with { Kind = 0 };
            });
    }

    internal static ImmutableDictionary<(byte Area, byte Kind), FiendwarTimerRow> FiendwarTimerSnapshot(AntonRaidBattleView view)
    {
        var rows = ImmutableDictionary.CreateBuilder<(byte, byte), FiendwarTimerRow>();
        if (view.Fiendwar is not { Failed: false, Cleared: false } field) return rows.ToImmutable();
        foreach (var row in field.Timers.OrderBy(row => row.Key.Kind, StringComparer.Ordinal))
        {
            // 00D2D160：0写入兵线槽；2–9共用设施槽。预警另由1524发布，不覆盖推进倒计时。
            byte? kind = row.Key.Kind switch
            {
                "move_enemy" => 0,
                "top_create_enemy" or "mid_create_enemy" or "bot_create_enemy" or
                "top_restore_center" or "mid_restore_center" or "bot_restore_center" => 2,
                _ => null
            };
            if (kind is { } value) Add(row.Key.Area, value, row.Value);
        }
        foreach (var row in view.FiendwarDungeonTimers) Add(row.Key, 12, row.Value);
        return rows.ToImmutable();

        void Add(int area, byte kind, FiendwarTimer timer)
        {
            // kind12虽不做减法，其00D28700→00DE8C90消费者仍按deadline-now显示剩余秒。
            byte id = checked((byte)area);
            rows[(id, kind)] = new(id, kind, checked((uint)timer.End.ToUnixTimeSeconds()),
                checked((uint)Math.Ceiling((timer.End - timer.Started).TotalSeconds)));
        }
    }

    internal static FiendwarTimerRow[] FiendwarTimerDelta(
        ImmutableDictionary<(byte Area, byte Kind), FiendwarTimerRow> before,
        ImmutableDictionary<(byte Area, byte Kind), FiendwarTimerRow> after) =>
        before.Keys.Where(key => !after.ContainsKey(key)).OrderBy(key => key.Area).ThenBy(key => key.Kind)
            .Select(key => new FiendwarTimerRow(key.Area, key.Kind, 0, 0))
            .Concat(after.OrderBy(row => row.Key.Area).ThenBy(row => row.Key.Kind)
                .Where(row => !before.TryGetValue(row.Key, out var old) || old != row.Value).Select(row => row.Value)).ToArray();
}
