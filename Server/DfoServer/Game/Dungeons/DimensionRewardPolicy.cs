using System.Collections.Concurrent;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 名册与排除表来自PVF；死亡概率及数量按本服规则，翻牌仍使用原资源。
internal sealed class DimensionRewardPolicy(DimensionDungeonCatalog dimension, DimensionRewardCatalog catalog, ItemCatalog items, DimensionDropRules? configuredRules = null)
{
    public const string Rule = "otherverse-rewards-server-compat-v1";
    public const string AuraRule = "otherverse-aura-equal-local-v1";
    public const string HolyAuraRule = "otherverse-holy-aura-personal-v1";
    public const string BossMaterialRule = "otherverse-boss-material-personal-v1";
    public const string CardSetRule = "otherverse-card-set-only-v1";
    public const string IfliusRule = "otherverse-iflius-video-v1";
    public const string LotusRule = "otherverse-lotus-boss-v1";
    public const string LotusDummyRule = "otherverse-lotus-dummy-no-base-v1";
    const int LotusBossMonsterId = 107005028;
    const int LotusDummyMonsterId = 107005037;
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
        if (!dimension.Tiers.ContainsKey(dungeon)) throw new InvalidDataException("异界翻牌缺副本档位");
        // 本人查询确认：现代异界通关翻牌必出套装，不再回退裂魂散件。
        var pool = Pool(job, grow, true);
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
        // 基础PVF计划已含装备/材料骰；异界专用规则改为替换这两类，避免同一怪同时发基础骰与专用骰。
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
        bool terminal = monster.SourceMap is { } map && entry.IsTerminalMap(map);
        // 罗特斯宫殿终点同时生成[dummy]阶段演员107005037与真实长脚罗特斯107005028；dummy只保留金币。
        bool lotusDummy = terminal && monster.MonsterId == LotusDummyMonsterId &&
            (monster.IsDummyMapBoss || monster.IsScriptedBoss);
        if (lotusDummy)
            return new(plan.ResourceSource + ";" + LotusDummyRule,
                plan.Trials.Where(trial => trial.Category == DungeonDropCategory.Gold).ToImmutableArray(),
                plan.ReferenceQuality, plan.Amplification);
        bool lotusBoss = terminal && monster.MonsterId == LotusBossMonsterId;
        bool boss = terminal && (monster.IsBoss || lotusBoss);
        bool named = !boss && (monster.Source?.IsNamed == true || entry.NamedMonsters.Contains(monster.MonsterId));
        bool iflius = monster.MonsterId is 107000256 or 107000257 or 107000258;
        if (!boss && !named && !iflius) return plan;
        // 基础PVF计划已含装备/材料骰；异界专用规则改为替换这两类，避免同一怪同时发基础骰与专用骰。
        var trials = plan.Trials.Where(trial => trial.Category is not (DungeonDropCategory.Equipment or DungeonDropCategory.Materials or DungeonDropCategory.MonsterItems)).ToImmutableArray().ToBuilder();
        if (iflius)
        {
            // 依普利乌斯是随机奖励怪物，不是终点领主；截图显示基础材料按多个独立堆叠掉落。
            AddMaterialStacks(3311, 3, 10);
            AddMaterialStacks(10303569, 4, 6);
            AddMaterialStacks(3330, 4, 5);
            AddMaterialStacks(10000204, 2, 8);
            var rare = dimension.HiddenRewards.Where(row => row.Tier == admission.Tier)
                .Select(row => new DungeonWeightedDropItem(items.Get(row.Item), row.Value)).ToArray();
            if (rare.Length > 0)
                trials.Add(new(1, new DungeonItemDropPool(rare), 1, 1, ProbabilityDenominator: 1));
            return new(plan.ResourceSource + ";" + ActiveRule + ";" + DimensionRewardCatalog.PoolPath + ";" + IfliusRule,
                trials, plan.ReferenceQuality, plan.Amplification);
        }
        var profession = entry.DimensionRewardProfession ?? throw new InvalidDataException("异界掉落缺少本人冻结职业");
        var tier = rules.Tiers[admission.Tier];
        // 材料按当前档位资源发放。绿名固定3311×7、10303569×5；领主不保留基础1份。
        if (boss)
        {
            AddMaterialStacks(3311, tier.Boss.ShardStacks, tier.Boss.ShardStackSize);
            if (dimension.Materials.Any(row => row.Tier == admission.Tier && row.Item == 10303569))
                AddMaterialStacks(10303569, tier.Boss.MemoryStacks, tier.Boss.MemoryStackSize);
        }
        else
        {
            foreach (var material in dimension.Materials.Where(row => row.Tier == admission.Tier))
            {
                int count = material.Item switch
                {
                    3311 => tier.Named.Shards,
                    10303569 => tier.Named.Memories,
                    _ => 1
                };
                AddMaterial(material.Item, count);
            }
        }
        // 本人拍板：绿名每次额外100%掉落1个普通三色气息，三色等权各1/3。
        if (named)
        {
            trials.Add(new(1, new DungeonItemDropPool([
                new DungeonWeightedDropItem(items.Get(1254), 1),
                new DungeonWeightedDropItem(items.Get(1255), 1),
                new DungeonWeightedDropItem(items.Get(1256), 1)]), 1, 1, ProbabilityDenominator: 1));
            // 本人拍板：难度1/2/3追加神圣气息概率1%/5%/10%，触发后三色等权抽1个。
            if (tier.Named.HolyAuraPercent > 0)
                trials.Add(new(tier.Named.HolyAuraPercent, new DungeonItemDropPool([
                    new DungeonWeightedDropItem(items.Get(10090758), 1),
                    new DungeonWeightedDropItem(items.Get(10090759), 1),
                    new DungeonWeightedDropItem(items.Get(10090760), 1)]), 1, 1, ProbabilityDenominator: 100));
        }

        if (rules.ExtraEntryEquipment || admission.DailyBefore.DailyUsed < admission.Catalog.BaseDailyLimit)
        {
            var normal = Pool(profession.Job, profession.Grow, false);
            var set = Pool(profession.Job, profession.Grow, true);
            int rate = rules.SetRateSource == "card" ? catalog.CardRates[admission.Tier].SetPercent : boss ? tier.Boss.Set : tier.Named.Set;
            int count = boss ? tier.Boss.Count : 1;
            int equip = boss ? 100 : tier.Named.Equip;
            if (EquipmentTrial(normal, set, equip, rate) is { } trial)
                for (int index = 0; index < count; index++) trials.Add(trial);
        }
        if (boss && admission.Tier == 2 && !catalog.Legendary.IsEmpty)
            trials.Add(new(1, new DungeonItemDropPool(catalog.Legendary.Select(id => new DungeonWeightedDropItem(items.Get(id), 1))),
                1, 1, ProbabilityDenominator: 100));

        string source = plan.ResourceSource + ";" + ActiveRule + ";" + DimensionRewardCatalog.PoolPath;
        if (named) source += ";" + AuraRule + ";" + HolyAuraRule;
        if (boss) source += ";" + BossMaterialRule;
        if (lotusBoss) source += ";" + LotusRule;

        return new(source, trials, plan.ReferenceQuality, plan.Amplification);

        void AddMaterial(int id, int count)
        {
            if (count > 0) trials.Add(new(1, new DungeonItemDropPool([new DungeonWeightedDropItem(items.Get(id), 1)]),
                count, count, ProbabilityDenominator: 1));
        }

        void AddMaterialStacks(int id, int stacks, int size)
        {
            if (stacks <= 0 || size <= 0) return;
            trials.Add(new(1, new DungeonItemDropPool([new DungeonWeightedDropItem(items.Get(id), 1)]),
                size, size, ProbabilityDenominator: 1, Attempts: stacks));
        }
    }

    // 互斥套装分支在装备触发之后抽样；资源缺套装时只回散件，不制造另一类别。
    internal static DungeonDropTrial? EquipmentTrial(DungeonItemDropPool normal, DungeonItemDropPool set, int equip, int rate)
    {
        if (set.TotalWeight == 0) set = normal;
        if (equip == 0 || normal.TotalWeight == 0 && set.TotalWeight == 0) return null;
        return new(equip, null, 1, 1, ProbabilityDenominator: 100,
            RarityPools: [new(DungeonDropRatePlan.RarityDenominator, normal,
                set.TotalWeight > 0 && rate > 0 ? set : null, set.TotalWeight > 0 ? rate * 10 : 0)],
            Category: DungeonDropCategory.Equipment);
    }

}
