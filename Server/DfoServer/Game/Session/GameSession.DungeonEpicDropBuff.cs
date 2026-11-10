using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed record DungeonEpicBuffDisplay(int BuffId, int Value);
internal sealed record DungeonEpicBuffProjectionReceipt(ImmutableArray<int> RemovedIds, ImmutableArray<DungeonEpicBuffDisplay> AddedBuffs,
    Func<bool> IsCurrent, Action MarkSent)
{
    public int Removed => RemovedIds.FirstOrDefault();
    public int Added => AddedBuffs.FirstOrDefault()?.BuffId ?? 0;
}

internal sealed partial class GameSession
{
    long dungeonEpicProjectionEpoch = -1;
    long dungeonEpicProjectionVersion;
    ImmutableArray<DungeonEpicBuffDisplay> projectedDungeonEpicBuffs = [];
    internal int CurrentDungeonEpicBuffId => DisplayVariousBuffs((timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds())
        .FirstOrDefault()?.BuffId ?? 0;

    ImmutableArray<DungeonEpicDropBuffDefinition> ActiveCompositeDropBuffs(long now) => DisplayVariousBuffs(now)
        .Where(row => formalDungeon is { } run &&
            (row.Lifetime != VariousBuffLifetime.DungeonEntries || run.VariousBuffEntryEffects.Any(active => active.BuffId == row.BuffId)) &&
            VariousBuffApplies(row, run.Entry)).Select(row =>
        {
            var value = DungeonEpicDropBuffDefinition.Convert(consumables!.VariousBuffs.Definitions[row.BuffId]);
            return value with { Resource = null, BossDropPercent = checked(value.BossDropPercent * row.Stacks),
                Rates = value.Rates.ToImmutableDictionary(pair => pair.Key, pair => checked(pair.Value * row.Stacks)) };
        }).ToImmutableArray();

    ImmutableArray<DungeonEpicBuffDisplay> CurrentDropBuffDisplays()
    {
        long now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        // 旧计时账本只补显示，不并入上方通用掉落计算；星空、白斯保留各自专用投影。
        var legacy = LegacyVariousBuffs(now).Where(row => row.Lifetime == VariousBuffLifetime.Timed &&
                consumables?.HellLegendEpicDropBuffs.ContainsKey(row.ItemId) != true)
            .GroupBy(row => row.BuffId).Select(group => group.MaxBy(row => row.UntilMilliseconds)!);
        return DisplayVariousBuffs(now).Concat(legacy).DistinctBy(row => row.BuffId)
            .Select(row => new DungeonEpicBuffDisplay(row.BuffId,
            row.Lifetime == VariousBuffLifetime.Timed ? checked((int)(row.UntilMilliseconds / 1000)) : row.Remaining))
            .OrderBy(buff => buff.BuffId).ToImmutableArray();
    }

    public DungeonEpicBuffProjectionReceipt? PlanDungeonEpicBuffProjection(bool force = false) => owner.Execute(() =>
    {
        if (Selected is not { } character) return null;
        if (dungeonEpicProjectionEpoch != SelectionEpoch)
        {
            dungeonEpicProjectionEpoch = SelectionEpoch;
            projectedDungeonEpicBuffs = [];
        }
        var desired = CurrentDropBuffDisplays();
        if (desired.SequenceEqual(projectedDungeonEpicBuffs) && (!force || desired.IsEmpty) && pendingVariousBuffRemovals.IsEmpty) return null;
        var removed = projectedDungeonEpicBuffs.Where(old => !desired.Any(buff => buff.BuffId == old.BuffId))
            .Select(buff => buff.BuffId).Concat(pendingVariousBuffRemovals).Distinct().Order().ToImmutableArray();
        var added = desired.Where(buff => force || pendingVariousBuffRemovals.Contains(buff.BuffId) || !projectedDungeonEpicBuffs.Contains(buff)).ToImmutableArray();
        long epoch = SelectionEpoch, version = ++dungeonEpicProjectionVersion;
        var phase = Phase;
        bool Current() => owner.Execute(() => owner.IsCurrent && Selected?.Id == character.Id && SelectionEpoch == epoch &&
            Phase == phase && dungeonEpicProjectionVersion == version && CurrentDropBuffDisplays().SequenceEqual(desired));
        void MarkSent() => owner.Execute(() =>
        {
            if (Current()) { projectedDungeonEpicBuffs = desired; pendingVariousBuffRemovals = ImmutableHashSet<int>.Empty; }
            return true;
        });
        return new DungeonEpicBuffProjectionReceipt(removed, added, Current, MarkSent);
    });
}
