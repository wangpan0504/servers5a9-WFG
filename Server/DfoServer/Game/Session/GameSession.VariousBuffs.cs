using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    ImmutableArray<VariousBuffState> transientVariousBuffs = [];
    ImmutableHashSet<int> pendingVariousBuffRemovals = ImmutableHashSet<int>.Empty;

    ImmutableArray<VariousBuffState> LiveVariousBuffs(long now) => (Selected?.Inventory.VariousBuffs ?? [])
        .Concat(transientVariousBuffs).Where(row => row.Active(now)).OrderBy(row => row.BuffId).ToImmutableArray();

    ImmutableArray<VariousBuffState> DisplayVariousBuffs(long now)
    {
        var current = formalDungeon?.VariousBuffEntryEffects ?? [];
        var ids = current.Select(row => row.BuffId).ToHashSet();
        return LiveVariousBuffs(now).Where(row => !ids.Contains(row.BuffId)).Concat(current.Where(row => row.Active(now)))
            .Where(row => consumables?.VariousBuffs.Definitions.TryGetValue(row.BuffId, out var definition) == true && definition.Supported &&
                !definition.ExceptChannels.Contains(ChannelEntry?.Type ?? 0)).OrderBy(row => row.BuffId).ToImmutableArray();
    }

    ImmutableArray<VariousBuffState> LegacyVariousBuffs(long now)
    {
        if (Selected is not { } character || consumables is null) return [];
        var rows = ImmutableArray.CreateBuilder<VariousBuffState>();
        foreach (var state in character.Inventory.ConsumableStates.Where(row => row.Kind == ConsumableStateKind.Effect && row.Active(now)))
            if (consumables.VariousBuffs.Items.TryGetValue(state.ItemId, out var definition))
                rows.Add(new(definition.BuffId, state.ItemId, VariousBuffLifetime.Timed, state.UntilMilliseconds, 0));
        if (character.Inventory.BossSelectionBuff is { } boss && boss.Active(now))
            rows.Add(new(boss.Definition.BuffId, boss.ItemId, VariousBuffLifetime.EffectApplications, boss.UntilMilliseconds, boss.Remaining));
        return rows.ToImmutable();
    }

    VariousBuffGrantPlan PlanVariousBuffGrant(RecoveryConsumableDefinition definition, DateTimeOffset now)
    {
        var binding = definition.VariousBuff!;
        // 覆盖必须看到整份共享定义，不能只在本STK的概率候选里寻找旧Buff的组信息。
        if (consumables is not null) binding = binding with { Definitions = consumables.VariousBuffs.Definitions.SetItems(binding.Definitions) };
        return VariousBuffPolicy.Grant(Selected!.Inventory.VariousBuffs, transientVariousBuffs,
            LegacyVariousBuffs(now.ToUnixTimeMilliseconds()), binding, definition.VariousDurationMilliseconds,
            definition.EffectMaintenance, now, System.Security.Cryptography.RandomNumberGenerator.GetInt32,
            (timeProvider ?? TimeProvider.System).LocalTimeZone);
    }

    void TrackVariousBuffRemovals(VariousBuffGrantPlan grant, RecoveryConsumableDefinition source)
    {
        var incoming = source.VariousBuff is not null ? grant.Granted :
            new[] { source.BossSelectionBuff?.BuffId ?? source.EpicDropBuff?.BuffId ?? source.HellLegendEpicDropBuff?.BuffId ??
                source.VillageMoveSpeedBuff?.BuffId ?? 0 }.Where(id => id > 0).ToImmutableArray();
        var ids = grant.Removed.ToHashSet();
        if (source.VariousBuff is not null && consumables is not null)
            foreach (int id in incoming)
                if (consumables.VariousBuffs.Definitions.TryGetValue(id, out var definition) && (!definition.Overlap || definition.OverlapMax == 1))
                    foreach (var old in consumables.VariousBuffs.Definitions.Values.Where(other => other.SystemGroup == definition.SystemGroup))
                        if (source.VariousBuff is not null || !incoming.Contains(old.BuffId)) ids.Add(old.BuffId);
        if (source.VariousBuff is null) ids.ExceptWith(incoming);
        pendingVariousBuffRemovals = pendingVariousBuffRemovals.Union(ids);
    }

    InventoryPlan WithVariousBuffGrant(InventoryPlan plan, VariousBuffGrantPlan grant)
    {
        var removed = grant.Removed.ToHashSet();
        var after = plan.After with
        {
            VariousBuffs = grant.Saved,
            ConsumableStates = [.. plan.After.ConsumableStates.Where(row => row.Kind != ConsumableStateKind.Effect ||
                consumables?.VariousBuffs.Items.TryGetValue(row.ItemId, out var definition) != true || definition is null || !removed.Contains(definition.BuffId))],
            BossSelectionBuff = plan.After.BossSelectionBuff is { } boss && removed.Contains(boss.Definition.BuffId) ? null : plan.After.BossSelectionBuff
        };
        return plan.WithAfter(after);
    }

    VariousBuffGrantPlan? PlanLegacyVariousCoverage(RecoveryConsumableDefinition definition, DateTimeOffset at)
    {
        if (consumables is null) return null;
        int id = definition.BossSelectionBuff?.BuffId ?? definition.EpicDropBuff?.BuffId ??
            definition.HellLegendEpicDropBuff?.BuffId ?? definition.VillageMoveSpeedBuff?.BuffId ?? 0;
        if (id == 0) return null;
        if (!consumables.VariousBuffs.Definitions.TryGetValue(id, out var incoming)) return null;
        long now = at.ToUnixTimeMilliseconds();
        var removed = VariousBuffPolicy.Covered(LiveVariousBuffs(now).Concat(LegacyVariousBuffs(now)), incoming, consumables.VariousBuffs.Definitions);
        return new(Selected!.Inventory.VariousBuffs.Where(row => row.Active(now) && !removed.Contains(row.BuffId)).ToImmutableArray(),
            transientVariousBuffs.Where(row => row.Active(now) && !removed.Contains(row.BuffId)).ToImmutableArray(), removed, []);
    }

    (ImmutableArray<VariousBuffState> Active, ImmutableArray<VariousBuffState> Saved, ImmutableArray<VariousBuffState> Transient)
        PlanVariousBuffEntry(DungeonEntryPlan entry, DateTimeOffset at)
    {
        long now = at.ToUnixTimeMilliseconds();
        var active = LiveVariousBuffs(now).Where(row => row.Lifetime == VariousBuffLifetime.DungeonEntries &&
            VariousBuffApplies(row, entry)).ToImmutableArray();
        var consumed = active.Select(row => row.BuffId).ToHashSet();
        ImmutableArray<VariousBuffState> Next(IEnumerable<VariousBuffState> rows) => rows.Where(row => row.Active(now))
            .ToImmutableArray();
        return (active, VariousBuffPolicy.Consume(Next(Selected!.Inventory.VariousBuffs), VariousBuffLifetime.DungeonEntries, consumed),
            VariousBuffPolicy.Consume(Next(transientVariousBuffs), VariousBuffLifetime.DungeonEntries, consumed));
    }

    bool VariousBuffApplies(VariousBuffState state, DungeonEntryPlan entry)
    {
        if (consumables?.VariousBuffs.Definitions.TryGetValue(state.BuffId, out var definition) != true || definition is null || !definition.Supported ||
            !DungeonEpicDropBuffPolicy.ConsumesEntry(entry) || definition.ExceptChannels.Contains(ChannelEntry?.Type ?? 0) ||
            entry.PartyMemberCount < definition.MinimumPartyMembers || entry.PartyMemberCount > definition.MaximumPartyMembers) return false;
        if (definition.ScopeRules.IsEmpty) return true;
        return definition.ScopeRules.Any(scope => scope.Kind switch
        {
            VariousBuffDungeonScope.All => true,
            VariousBuffDungeonScope.Level => entry.BasisLevel is { } level && level >= scope.Values[0] && level <= scope.Values[1],
            VariousBuffDungeonScope.HellLegend => entry.IsHellLegend,
            VariousBuffDungeonScope.Hell => entry.HellParty is not null || entry.IsHellLegend || entry.IsInfernoParty || entry.IsCosmo,
            VariousBuffDungeonScope.DungeonIndex => scope.Values.Contains(checked((int)entry.DungeonId)),
            VariousBuffDungeonScope.DungeonType => entry.VariousBuffDungeonType is { } type && scope.Values.Contains(type),
            VariousBuffDungeonScope.ExceptDungeonType => entry.VariousBuffDungeonType is { } type && !scope.Values.Contains(type),
            _ => false
        });
    }

    public DateTimeOffset NextVariousBuffWake(DateTimeOffset now) => owner.Execute(() =>
    {
        long current = now.ToUnixTimeMilliseconds();
        long next = (Selected?.Inventory.VariousBuffs ?? []).Concat(transientVariousBuffs).Concat(LegacyVariousBuffs(current))
            .SelectMany(row => new[] { row.UntilMilliseconds, row.DailyUntilMilliseconds })
            .Where(value => value > current).DefaultIfEmpty(long.MaxValue).Min();
        return next == long.MaxValue ? DateTimeOffset.MaxValue : DateTimeOffset.FromUnixTimeMilliseconds(next);
    });

    public InventoryResult? AdvanceVariousBuffs() => owner.Execute<InventoryResult?>(() =>
    {
        if (!CanUseConsumable() || Selected is not { } character) return null;
        long now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        var saved = character.Inventory.VariousBuffs.Where(row => row.Active(now)).ToImmutableArray();
        var transient = transientVariousBuffs.Where(row => row.Active(now)).ToImmutableArray();
        if (saved.SequenceEqual(character.Inventory.VariousBuffs)) { transientVariousBuffs = transient; return null; }
        var result = CommitInventory(character, new(true, 0, new(character.Inventory,
            character.Inventory with { VariousBuffs = saved }, "various_buff_expired")), 123);
        if (result.Accepted) transientVariousBuffs = transient;
        return result;
    });
}
