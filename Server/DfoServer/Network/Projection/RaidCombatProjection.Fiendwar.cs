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
            var identity = RaidCombatDisplayIdentity.Capture(actor, view);
            bool Show() => current() && context.RaidDisplay != identity;
            if (view.State == AntonRaidBattleState.Reward && context.Session.RaidCombatResult(view) is { } result)
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_RESULT, RaidCombatPackets.Result(result), "fiendwar_result", Show);
            // 00D30460的3与587的3调用相同胜利窗口；type2必须走1523，不能重装其它团本状态。
            byte state = view.PhaseExpired ? (byte)5 : view.State is AntonRaidBattleState.Reward or AntonRaidBattleState.Completed ? (byte)3 : (byte)2;
            context.Sender.Post(0, (ushort)NotiPacketType.FIEND_WAR_STATE, FiendwarPackets.State(state, 0),
                "fiendwar_state", Show, () => context.RaidDisplay = identity);
            return true;
        });
    }

    internal static void PostFiendwarSnapshot(GameConnectionContext context, AntonRaidBattle battle,
        AntonRaidBattleView view, Func<bool> current)
    {
        if (battle.Kind != RaidKind.Fiendwar || view.Fiendwar is not { } field) return;
        bool Guard() => current() && battle.IsCurrent(view);
        if (view.State == AntonRaidBattleState.Fighting && !view.PhaseExpired && !view.ClockFaulted && !field.Failed && !field.Cleared)
        {
            // 00DE9128读取597槽0；type45经01DD1752/01DD187C按同一秒时钟读写，1522本身不设置时间。
            // 入场、重返和战况重放共用真实deadline，排队期间流逝的时间不能重新赠送。
            context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_REMAIN_TIME, 5,
                () =>
                {
                    var remaining = field.Deadline - battle.FiendwarNow;
                    return remaining > TimeSpan.Zero
                        ? RaidCombatPackets.PhaseRemaining(checked((uint)Math.Ceiling(remaining.TotalSeconds))) : null;
                },
                "fiendwar_remaining", Guard);
        }
        // 差量在FIFO真正写帧时与已写展示比较；旧快照跳过不能吞掉后续必要删除。
        context.Sender.PostPrepared(0, (ushort)NotiPacketType.FIEND_WAR_BOARD_USER_INFO, 1 + 16 * 13,
            () => FiendwarPackets.Users(context.FiendwarUsers.Where(row => view.FiendwarPositions.GetValueOrDefault(row.Key, -1) != row.Value)
                .Select(row => new FiendwarUserRow(2, checked((byte)row.Value), row.Key, 0))
                .Concat(view.FiendwarPositions.OrderBy(row => row.Key)
                    .Select(row => new FiendwarUserRow(1, checked((byte)row.Value), row.Key, 0))).ToArray()),
            "fiendwar_users", Guard, allowShorter: true, written: () => context.FiendwarUsers = view.FiendwarPositions);
        context.Sender.Post(0, (ushort)NotiPacketType.FIEND_WAR_BOARD_WARNING,
            FiendwarPackets.Warnings(field.Areas.OrderBy(row => row.Key).Select(row =>
                (checked((byte)row.Key), row.Value.Warning ? (byte)1 : (byte)0)).ToArray()), "fiendwar_warnings", Guard);
        foreach (bool enemy in new[] { false, true })
        {
            var units = field.Areas.Where(row => (enemy ? row.Value.Enemy : row.Value.Monster) is not null)
                .ToImmutableDictionary(row => row.Key, row => (enemy ? row.Value.Enemy : row.Value.Monster)!.Value);
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
        context.Sender.Post(0, (ushort)NotiPacketType.FIEND_WAR_SET_SYMBOL,
            FiendwarPackets.Symbols(battle.SymbolSnapshot(view).Select(row => (checked((uint)row.Id), unchecked((uint)row.Value))).ToArray()),
            "fiendwar_symbols", Guard);
        if (view.FiendwarSupport is { } support)
        {
            var identity = (support.Instance, support.Sequence);
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_SITUATION_NOTICE, FiendwarPackets.SupportNotice(support.Lane),
                "fiendwar_support_notice", () => Guard() && context.FiendwarSupportDisplay != identity &&
                    context.Session.CanPublishFiendwarSupport(support), () => context.FiendwarSupportDisplay = identity);
        }
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
