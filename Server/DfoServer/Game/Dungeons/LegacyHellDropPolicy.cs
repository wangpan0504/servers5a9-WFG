using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Dungeons;

// 旧地区奖励种类、等级与地区身份经版本资料及PVF核对；概率沿用本服补全口径，不冒充原服精确值。
internal sealed class LegacyHellDropPolicy
{
    public const string Rule = "legacy-hell-rewards-local-v1";
    readonly ImmutableDictionary<uint, (int Minimum, int Maximum, int Crystal)> regions;
    readonly ImmutableDictionary<int, ImmutableArray<DungeonDropTrial>> bonuses;

    public LegacyHellDropPolicy(TownCatalog towns, Func<int, ItemTemplate> resolveItem, HarlemAbyssBonusRules rules,
        IEnumerable<DungeonDefinition>? infernoDungeons = null)
    {
        rules.Validate();
        var routes = ImmutableDictionary.CreateBuilder<uint, (int, int, int)>();
        foreach (var map in towns.DungeonMaps.Values.Where(map => map.HellDungeon))
        {
            // 先锋战区晶石不是寂静城宝珠；后者是本地500邀请函的阴沉宝珠。
            (int Minimum, int Maximum, int Crystal)? region = map.Path.ToLowerInvariant() switch
            {
                "worldmap/lufthafen.wdm" => (70, 75, 0),
                "worldmap/timegate.wdm" => (75, 80, 10088689),
                "worldmap/powerstation.wdm" => (80, 85, 10088690),
                "worldmap/castleofthedead.wdm" => (80, 85, 10146492),
                "worldmap/metrocenter.wdm" => (85, 90, 10159141),
                _ => null
            };
            if (region is not { } row) continue;
            foreach (var option in map.Options)
            {
                var value = (map.Path == "worldmap/powerstation.wdm" && option.DungeonId == 101 ? 75 : row.Minimum,
                    row.Maximum, row.Crystal);
                uint id = checked((uint)option.DungeonId);
                if (routes.TryGetValue(id, out var previous) && previous != value)
                    throw new InvalidDataException("旧地区深渊副本有冲突的地区声明: " + id);
                routes[id] = value;
            }
        }
        foreach (var dungeon in infernoDungeons ?? [])
        {
            if (!dungeon.IsInfernoDungeon) continue;
            var path = dungeon.Path.ToLowerInvariant();
            (int, int, int) region = path switch
            {
                var value when value.StartsWith("dungeon/infernomode/inferno_seatrain", StringComparison.Ordinal) => (70, 75, 0),
                var value when value.StartsWith("dungeon/infernomode/inferno_timegate", StringComparison.Ordinal) => (75, 80, 0),
                var value when value.StartsWith("dungeon/infernomode/inferno_powerstation", StringComparison.Ordinal) => (80, 85, 0),
                var value when value.StartsWith("dungeon/infernomode/inferno_deadcastle", StringComparison.Ordinal) => (80, 85, 0),
                var value when value.StartsWith("dungeon/infernomode/inferno_metrocenter", StringComparison.Ordinal) => (85, 90, 0),
                _ => throw new NotSupportedException("未知地区的时空裂缝奖励: " + dungeon.Path)
            };
            routes[checked((uint)dungeon.Id)] = region;
        }
        regions = routes.ToImmutable();
        bonuses = regions.Values.Select(row => row.Crystal).Distinct().ToImmutableDictionary(id => id, id =>
        {
            var trials = ImmutableArray.CreateBuilder<DungeonDropTrial>();
            if (rules.Enabled)
            {
                if (rules.InvitationNumerator > 0)
                    trials.Add(new(rules.InvitationNumerator, Pool(HarlemAbyssBonusDropPolicy.InvitationId), 1,
                        rules.InvitationMaximumCount, ProbabilityDenominator: rules.InvitationDenominator, IndividualObjects: true));
                if (rules.SoulPermille > 0)
                    trials.Add(new(rules.SoulPermille, Pool(HarlemAbyssBonusDropPolicy.SoulId), 1, 1, ProbabilityDenominator: 1000));
                if (id > 0 && rules.CrystalPermille > 0)
                    trials.Add(new(rules.CrystalPermille, Pool(id), 1, 1, ProbabilityDenominator: 1000));
            }
            return trials.ToImmutable();
        });

        DungeonItemDropPool Pool(int id)
        {
            var item = resolveItem(id);
            if (item.Id != id || !DungeonDropCreation.Supports(item.Kind) || item.Kind == ItemKind.Equipment ||
                item.StackLimit <= 0 || !item.Expiration.SupportsCreation)
                throw new InvalidDataException("旧地区深渊奖励不是可新建的堆叠物品: " + id);
            return new(new DungeonWeightedDropItem[] { new(item, 1) });
        }
    }

    public (int Minimum, int Maximum)? EquipmentRange(DungeonEntryPlan entry) =>
        entry.IsOrdinary && !entry.IsPractice && (entry.HellParty is not null || entry.IsInfernoParty) &&
        regions.TryGetValue(entry.DungeonId, out var region) ? (region.Minimum, region.Maximum) : null;

    public bool MatchesOrdinaryReward(DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        EquipmentRange(entry) is not null && !entry.IsInfernoParty && !entry.IsHellLegend && !entry.IsCosmo && monster.HellWave is not null &&
        monster.HellRewardRolls > 0 && monster.CanReportDeath && !monster.IsFriendly && !monster.IsScriptedBoss;

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        if (!MatchesOrdinaryReward(entry, monster)) return plan;
        var trials = bonuses[regions[entry.DungeonId].Crystal];
        return trials.IsEmpty ? plan : new(plan.ResourceSource + ";" + Rule, plan.Trials.Concat(trials), plan.ReferenceQuality, plan.Amplification);
    }
}
