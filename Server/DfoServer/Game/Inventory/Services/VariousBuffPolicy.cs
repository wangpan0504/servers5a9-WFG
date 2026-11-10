using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed record VariousBuffGrantPlan(ImmutableArray<VariousBuffState> Saved, ImmutableArray<VariousBuffState> Transient,
    ImmutableArray<int> Removed, ImmutableArray<int> Granted);

internal static class VariousBuffPolicy
{
    internal const int DefaultDurationMilliseconds = 30 * 60 * 1000;

    public static ImmutableArray<VariousBuffState> Consume(ImmutableArray<VariousBuffState> rows, VariousBuffLifetime lifetime,
        IReadOnlySet<int> applied) => rows.Select(row => row.Lifetime == lifetime && applied.Contains(row.BuffId)
            ? row with { Remaining = row.Remaining - 1 } : row).Where(row => row.Lifetime == VariousBuffLifetime.Timed || row.Remaining > 0)
            .ToImmutableArray();
    public static ImmutableArray<int> Covered(IEnumerable<VariousBuffState> current, VariousBuffDefinition incoming,
        IReadOnlyDictionary<int, VariousBuffDefinition> definitions)
    {
        var same = current.Where(row => definitions.TryGetValue(row.BuffId, out var old) && old.SystemGroup == incoming.SystemGroup)
            .OrderBy(row => row.GrantedMilliseconds).ThenBy(row => row.BuffId).ToArray();
        if (!incoming.Overlap || incoming.OverlapMax == 1) return same.Select(row => row.BuffId).ToImmutableArray();
        return same.Where(row => row.BuffId == incoming.BuffId).Select(row => row.BuffId)
            .Concat(same.Where(row => row.BuffId != incoming.BuffId).Take(Math.Max(0,
                same.Count(row => row.BuffId != incoming.BuffId) - incoming.OverlapMax + 1)).Select(row => row.BuffId)).ToImmutableArray();
    }

    public static VariousBuffGrantPlan Grant(ImmutableArray<VariousBuffState> saved, ImmutableArray<VariousBuffState> transient,
        ImmutableArray<VariousBuffState> legacy, VariousBuffBinding binding, int? milliseconds, byte maintenance, DateTimeOffset at,
        Func<int, int> draw, TimeZoneInfo? timeZone = null)
    {
        if (!binding.Ready) throw new InvalidOperationException("Buff关联或效果未就绪");
        long now = at.ToUnixTimeMilliseconds();
        var all = saved.Concat(transient).Concat(legacy).Where(row => row.Active(now)).GroupBy(row => row.BuffId)
            .ToDictionary(group => group.Key, group => group.First());
        var original = all.Keys.ToHashSet();
        var savedIds = saved.Select(row => row.BuffId).ToHashSet();
        var transientIds = transient.Select(row => row.BuffId).ToHashSet();
        var requested = binding.Association.Select(draw);
        foreach (int id in requested)
        {
            var definition = binding.Definitions[id];
            all.TryGetValue(id, out var previous);
            foreach (int removed in Covered(all.Values, definition, binding.Definitions)) all.Remove(removed);
            long until = 0;
            if (definition.Timed)
            {
                // STK按毫秒、ETC按秒；只有未声明才回落，显式0仍表示永久。
                long duration = milliseconds ?? (definition.ApplyParameters.IsEmpty ? DefaultDurationMilliseconds :
                    checked((long)definition.ApplyParameters[0] * 1000));
                if (duration > 0) until = Math.Min(checked(now + duration), (long)ItemExpiration.MaximumUnixTime * 1000);
            }
            var lifetime = definition.Timed ? VariousBuffLifetime.Timed : definition.ApplyType == "effect_apply"
                ? VariousBuffLifetime.EffectApplications : VariousBuffLifetime.DungeonEntries;
            var state = new VariousBuffState(id, binding.Association.ItemId, lifetime, until,
                definition.Timed ? 0 : definition.ApplyParameters[0], definition.DeleteDaily ? NextDailyReset(at, timeZone).ToUnixTimeMilliseconds() : 0)
            {
                GrantedMilliseconds = now,
                Stacks = definition.Overlap && previous is not null ? Math.Min(definition.OverlapMax, previous.Stacks + 1) : 1
            };
            all[id] = state;
            if (definition.Save || definition.Timed && (maintenance & (byte)ConsumableMaintenance.Reselect) != 0)
            { savedIds.Add(id); transientIds.Remove(id); }
            else { transientIds.Add(id); savedIds.Remove(id); }
        }
        // 旧专用状态只参与覆盖判断，不把它复制到通用账本；实际清除与源消费由同一计划提交。
        var added = requested.ToHashSet();
        var remaining = all.Values.Where(row => savedIds.Contains(row.BuffId) || transientIds.Contains(row.BuffId))
            .OrderBy(row => row.BuffId).ToArray();
        return new(remaining.Where(row => savedIds.Contains(row.BuffId)).ToImmutableArray(),
            remaining.Where(row => transientIds.Contains(row.BuffId)).ToImmutableArray(),
            original.Where(id => !all.ContainsKey(id) || added.Contains(id)).Order().ToImmutableArray(),
            requested.Where(all.ContainsKey).Distinct().ToImmutableArray());
    }

    public static DateTimeOffset NextDailyReset(DateTimeOffset at, TimeZoneInfo? zone = null) =>
        DfoServer.GameWorld.Time.ServiceDay.NextReset(at, zone ?? TimeZoneInfo.Local);
}
