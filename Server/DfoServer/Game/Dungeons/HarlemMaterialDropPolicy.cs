using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 用户授权的本服材料规则：物品身份沿当前PVF；缺失的数量和概率不冒充原版数据。
internal sealed record HarlemMaterialDropRules
{
    public bool Enabled { get; init; } = true;
    public ImmutableArray<int> NormalFragments { get; init; } = [9, 10, 12, 13, 15];
    public int LegendFragments { get; init; } = 15;
    public ImmutableArray<int> SkyWishes { get; init; } = [2, 3, 4, 4, 5];
    public int CosmoMaterialMultiplier { get; init; } = 2;
    public int DawnFragments { get; init; } = 200;
    public int DawnPurgedCrystallines { get; init; } = 120;
    public int DawnPurgedPermille { get; init; } = 50;

    public static HarlemMaterialDropRules Parse(string json)
    {
        var rules = JsonSerializer.Deserialize<HarlemMaterialDropRules>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("哈林材料规则不能为空");
        rules.Validate();
        return rules;
    }

    public void Validate()
    {
        if (NormalFragments.IsDefault || NormalFragments.Length != 5 || NormalFragments.Any(count => count is < 1 or > 10000) ||
            LegendFragments is < 1 or > 10000 || DawnFragments is < 1 or > 10000 || DawnPurgedCrystallines is < 1 or > 10000 ||
            DawnPurgedPermille is < 0 or > 1000 || SkyWishes.IsDefault || SkyWishes.Length != 5 ||
            SkyWishes.Any(count => count is < 1 or > 10000) || CosmoMaterialMultiplier is < 1 or > 10 ||
            (long)LegendFragments * CosmoMaterialMultiplier > 10000 || (long)SkyWishes.Max() * CosmoMaterialMultiplier > 10000)
            throw new InvalidDataException("哈林材料规则须提供五档正数量、1至10000的材料数量及0至1000的净化分支概率");
    }
}

internal sealed class HarlemMaterialDropPolicy
{
    public const string Rule = "harlem-material-local-v2";
    public const int FragmentId = 10305893;
    public const int PurgedId = 10309039;
    public const int SkyWishId = 10305617;
    readonly HarlemMaterialDropRules rules;
    readonly ImmutableHashSet<uint> hellDungeons;
    readonly DungeonItemDropPool fragments;
    readonly DungeonDropTrial dawn;
    readonly DungeonItemDropPool skyWishes;
    readonly HarlemHellEpicDropPolicy? hellEpics;

    public HarlemMaterialDropPolicy(HarlemMaterialDropRules rules, IEnumerable<uint> hellDungeons, Func<int, ItemTemplate> resolveItem,
        HarlemHellEpicDropPolicy? hellEpics = null)
    {
        rules.Validate();
        this.rules = rules;
        this.hellDungeons = hellDungeons.ToImmutableHashSet();
        this.hellEpics = hellEpics;
        var fragment = Item(FragmentId);
        var purged = Item(PurgedId);
        fragments = new(new DungeonWeightedDropItem[] { new(fragment, 1) });
        skyWishes = new(new DungeonWeightedDropItem[] { new(Item(SkyWishId), 1) });
        var alternatives = new List<DungeonWeightedDropItem>();
        var counts = ImmutableDictionary.CreateBuilder<int, int>();
        if (rules.DawnPurgedPermille < 1000)
        {
            alternatives.Add(new(fragment, 1000 - rules.DawnPurgedPermille));
            counts.Add(FragmentId, rules.DawnFragments);
        }
        if (rules.DawnPurgedPermille > 0)
        {
            alternatives.Add(new(purged, rules.DawnPurgedPermille));
            counts.Add(PurgedId, rules.DawnPurgedCrystallines);
        }
        dawn = new(1000, new DungeonItemDropPool(alternatives), 1, 1,
            ProbabilityDenominator: 1000, ItemCounts: counts.ToImmutable());

        ItemTemplate Item(int id)
        {
            var template = resolveItem(id);
            if (template.Id != id || template.Kind is not (ItemKind.Material or ItemKind.Consumable or ItemKind.Profession) ||
                template.StackLimit <= 0 || !template.Expiration.SupportsCreation)
                throw new NotSupportedException("当前PVF不支持哈林材料新建: " + id);
            return template;
        }
    }

    public bool UsesHarlemFragments(DungeonEntryPlan entry) => rules.Enabled && entry.IsOrdinary &&
        !entry.IsPractice && entry.HellParty is not null && hellDungeons.Contains(entry.DungeonId);

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        if (!rules.Enabled || !entry.IsOrdinary || entry.IsPractice || monster.IsFriendly || !monster.CanReportDeath || monster.IsScriptedBoss)
            return plan;
        var rewards = new List<DungeonDropTrial>();
        if (UsesHarlemFragments(entry) && monster.HellWave is not null && monster.HellRewardRolls > 0)
        {
            if (entry.Difficulty >= rules.NormalFragments.Length) throw new InvalidDataException("哈林材料难度越界");
            int count = entry.IsHellLegend ? rules.LegendFragments : rules.NormalFragments[entry.Difficulty];
            // 每个材料各占一个地面对象；总数不随装备骰数增加，拾取仍走原账本。
            rewards.Add(new(1000, fragments, 1, 1, ProbabilityDenominator: 1000, Attempts: count));
            if (entry.IsHellLegend)
            {
                int wishes = rules.SkyWishes[entry.Difficulty];
                rewards.Add(new(1000, skyWishes, 1, 1, ProbabilityDenominator: 1000, Attempts: wishes));
            }
        }
        else if (hellEpics?.IsCosmoBoss(entry, monster) == true)
        {
            int count = checked(rules.LegendFragments * rules.CosmoMaterialMultiplier);
            int wishes = checked(rules.SkyWishes[entry.Difficulty] * rules.CosmoMaterialMultiplier);
            rewards.Add(new(1000, fragments, 1, 1, ProbabilityDenominator: 1000, Attempts: count));
            rewards.Add(new(1000, skyWishes, 1, 1, ProbabilityDenominator: 1000, Attempts: wishes));
        }
        // 当前530的真实Boss为41207中的Molech；不把绿名、dummy或其它副本的同模板怪当作奖励源。
        else if (entry.DungeonId == 530 && entry.BossMap.Id == 41207 && monster.MonsterId == 71030 && monster.IsBoss)
            rewards.Add(dawn);
        return rewards.Count == 0 ? plan : new(plan.ResourceSource + ";" + Rule, plan.Trials.Concat(rewards), plan.ReferenceQuality, plan.Amplification);
    }
}
