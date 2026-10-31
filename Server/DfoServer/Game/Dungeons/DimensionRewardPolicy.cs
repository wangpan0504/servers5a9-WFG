using System.Collections.Concurrent;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 名册与排除表来自PVF；死亡概率及数量按本服规则，翻牌仍使用原资源。
internal sealed class DimensionRewardPolicy(DimensionDungeonCatalog dimension, DimensionRewardCatalog catalog, ItemCatalog items, DimensionDropRules? configuredRules = null)
{
    public const string Rule = "otherverse-rewards-server-compat-v1";
    readonly DimensionDropRules rules = Validated(configuredRules ?? DimensionDropRules.Default);
    public string ActiveRule => rules.Rule;
    static DimensionDropRules Validated(DimensionDropRules value) { value.Validate(); return value; }
    readonly ConcurrentDictionary<(byte Job, int Grow, bool Set), DungeonItemDropPool> pools = new();

    DungeonItemDropPool Pool(byte job, byte grow, bool set) => pools.GetOrAdd((job, grow & 15, set), key =>
    {
        var ids = catalog.Professions.TryGetValue((key.Job, key.Grow), out var row) ? (key.Set ? row.Set : row.Normal) : [];
        return new(ids.Select(id => new DungeonWeightedDropItem(items.Get(id), 1)));
    });

    public ItemCore? Card(int dungeon, byte job, byte grow, Func<int, int> draw, DateTimeOffset now)
    {
        if (!dimension.Tiers.TryGetValue(dungeon, out int tier)) throw new InvalidDataException("异界翻牌缺副本档位");
        var rates = catalog.CardRates[tier];
        if (draw(100) >= rates.RewardPercent) return null;
        bool set = draw(100) < rates.SetPercent;
        var pool = Pool(job, grow, set);
        return pool.TotalWeight == 0 ? null : DungeonDropCreation.Create(pool.Select(draw(pool.TotalWeight)), 1, draw, now);
    }

    public ItemCore AncientCard(int dungeon, Func<int, int> draw, DateTimeOffset now, bool bonus = false)
    {
        if (!catalog.AncientBoxes.TryGetValue(dungeon, out var pair)) throw new InvalidDataException("远古缺通关奖励行");
        return DungeonDropCreation.Create(items.Get(bonus ? pair.BonusBox : pair.NormalBox), 1, draw, now);
    }

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        if (rules.Mode != "compat-v1") return ExtendV2(plan, entry, monster);
        // 现代异界终点也有带[dummy]的真实Boss；以本实例实际终点和死亡资格为准，不能沿普通材料规则一概剔除。
        if (entry.Dimension is not { } admission || !monster.CanReportDeath || monster.IsFriendly ||
            monster.AiCharacter is not null || !monster.IsBoss || monster.SourceMap is not { } map || !entry.IsTerminalMap(map)) return plan;
        var profession = entry.DimensionRewardProfession ?? throw new InvalidDataException("异界掉落缺少本人冻结职业");
        var trials = plan.Trials.ToBuilder();
        // 每个合法Boss发一份各组材料；表只声明物品，本服固定数量1，不把组号当数量。
        foreach (var material in dimension.Materials.Where(row => row.Tier == admission.Tier))
            trials.Add(new(1, new DungeonItemDropPool([new DungeonWeightedDropItem(items.Get(material.Item), 1)]), 1, 1, ProbabilityDenominator: 1));
        var pool = Pool(profession.Job, profession.Grow, false);
        if (pool.TotalWeight > 0) trials.Add(new(1, pool, 1, 1, ProbabilityDenominator: 1));
        // 当前资源没有传奇组触发率；本服明确采用终点Boss独立1%，全组等权且经过排除表。
        if (admission.Tier == 2 && !catalog.Legendary.IsEmpty)
            trials.Add(new(1, new DungeonItemDropPool(catalog.Legendary.Select(id => new DungeonWeightedDropItem(items.Get(id), 1))),
                1, 1, ProbabilityDenominator: 100));
        if (admission.HiddenBoss)
        {
            // Value按同档候选相对权重解释；一次选一件，不以权重当发放数量。
            var hidden = new DungeonItemDropPool(dimension.HiddenRewards.Where(row => row.Tier == admission.Tier)
                .Select(row => new DungeonWeightedDropItem(items.Get(row.Item), row.Value)));
            trials.Add(new(1, hidden, 1, 1, ProbabilityDenominator: 1));
        }
        return new(plan.ResourceSource + ";" + Rule + ";" + DimensionRewardCatalog.PoolPath,
            trials, plan.ReferenceQuality, plan.Amplification);
    }
    DungeonDropGenerationPlan ExtendV2(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        if (entry.IsPractice || entry.DungeonId is < 7165 or > 7179 || entry.Dimension is not { } admission ||
            !monster.CanReportDeath || monster.IsFriendly || monster.AiCharacter is not null) return plan;
        bool boss = monster.IsBoss && monster.SourceMap is { } map && entry.IsTerminalMap(map);
        bool named = !monster.IsBoss && (monster.Source?.IsNamed == true || entry.NamedMonsters.Contains(monster.MonsterId));
        if (!boss && !named) return plan;
        var profession = entry.DimensionRewardProfession ?? throw new InvalidDataException("异界掉落缺少本人冻结职业");
        var tier = rules.Tiers[admission.Tier];
        var trials = plan.Trials.ToBuilder();
        if (boss)
            foreach (var material in dimension.Materials.Where(row => row.Tier == admission.Tier)) AddMaterial(material.Item, 1);
        if (rules.ExtraEntryEquipment || admission.DailyBefore.DailyUsed < admission.Catalog.DailyLimit)
        {
            var normal = Pool(profession.Job, profession.Grow, false);
            var set = Pool(profession.Job, profession.Grow, true);
            int rate = rules.SetRateSource == "card" ? catalog.CardRates[admission.Tier].SetPercent : boss ? tier.Boss.Set : tier.Named.Set;
            int count = boss ? tier.Boss.Count : 1;
            int equip = boss ? 100 : tier.Named.Equip;
            if (EquipmentTrial(normal, set, equip, rate) is { } trial)
                for (int index = 0; index < count; index++) trials.Add(trial);
        }
        AddMaterial(3311, boss ? tier.Boss.Shards : tier.Named.Shards);
        if (boss && admission.Tier == 2 && !catalog.Legendary.IsEmpty)
            trials.Add(new(1, new DungeonItemDropPool(catalog.Legendary.Select(id => new DungeonWeightedDropItem(items.Get(id), 1))),
                1, 1, ProbabilityDenominator: 100));
        if (boss && admission.HiddenBoss)
            trials.Add(new(1, new DungeonItemDropPool(dimension.HiddenRewards.Where(row => row.Tier == admission.Tier)
                .Select(row => new DungeonWeightedDropItem(items.Get(row.Item), row.Value))), 1, 1, ProbabilityDenominator: 1));
        return new(plan.ResourceSource + ";" + ActiveRule + ";" + DimensionRewardCatalog.PoolPath,
            trials, plan.ReferenceQuality, plan.Amplification);

        void AddMaterial(int id, int count)
        {
            if (count > 0) trials.Add(new(1, new DungeonItemDropPool([new DungeonWeightedDropItem(items.Get(id), 1)]),
                count, count, ProbabilityDenominator: 1));
        }
    }

    // 互斥套装分支在装备触发之后抽样；资源缺套装时只回散件，不制造另一类别。
    internal static DungeonDropTrial? EquipmentTrial(DungeonItemDropPool normal, DungeonItemDropPool set, int equip, int rate)
    {
        if (set.TotalWeight == 0) set = normal;
        if (equip == 0 || normal.TotalWeight == 0 && set.TotalWeight == 0) return null;
        return new(equip, null, 1, 1, ProbabilityDenominator: 100,
            RarityPools: [new(DungeonDropRatePlan.RarityDenominator, normal,
                set.TotalWeight > 0 && rate > 0 ? set : null, set.TotalWeight > 0 ? rate * 10 : 0)]);
    }

}
