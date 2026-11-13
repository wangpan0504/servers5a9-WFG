using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 强袭专属掉落规则：终点领主固定材料；英雄之证装备仅隐藏终点独立掉落，普通怪不再产出污浊气息。
internal sealed class AssaultMaterialDropPolicy
{
    public const string Rule = "assault-material-local-v3";
    public const int MaterialId = 10305788;
    public const int CertificateId = 10305786;
    public static ImmutableArray<int> HeroProofItemIds { get; } = [
        101010932, 101040531, 101020741, 101030808, 101000788,
        102020523, 102040589, 102010617, 102000554, 102030522,
        104000593, 104010577, 104040593, 104020573, 104030577,
        106000540, 106020614, 106010571, 106030644, 106040512,
        108000543, 108010531, 108020449, 108040500, 108030552,
        109000511, 109010468, 109030360, 109040342,
        114000269, 114010251, 114020162, 114030162,
        116000174, 116010148, 116020150, 116030188,
        // 以下31件由英雄之证自选礼盒10307081～10307087的[equipment]内容展开。
        100050801, 100100702, 100150671, 100200646, 100250665,
        100060659, 100110645, 100160586, 100210610, 100260626,
        100070612, 100120604, 100170579, 100220579, 100270576,
        100080635, 100130616, 100180584, 100230584, 100280579,
        100090439, 100140434, 100190405, 100240412, 100290425,
        100300952, 100312646, 100322539,
        100344903, 100353086, 100390026
    ];
    public const int FreeCardCertificateCount = 55;

    readonly HashSet<uint> missions;
    readonly HashSet<uint> hiddenMissions;
    readonly DungeonDropTrial normalCertificate;
    readonly DungeonDropTrial normalMaterial;
    readonly DungeonDropTrial hiddenCertificate;
    readonly DungeonDropTrial hiddenMaterial;
    readonly DungeonDropTrial hiddenHeroProof;

    public AssaultMaterialDropPolicy(AssaultModeCatalog catalog, Func<int, ItemTemplate> resolve)
    {
        missions = catalog.Missions.Values.SelectMany(rows => rows).Select(id => checked((uint)id)).ToHashSet();
        hiddenMissions = catalog.Missions.TryGetValue(catalog.HiddenBaseDungeon, out var hidden)
            ? hidden.Select(id => checked((uint)id)).ToHashSet() : new HashSet<uint>();
        var material = resolve(MaterialId);
        var certificate = resolve(CertificateId);
        if (material is not { Kind: ItemKind.Material, Grade: 1, Rarity: 2, CreationRate: 0 } ||
            certificate is not { Kind: ItemKind.Material, StackLimit: > 0, Expiration.SupportsCreation: true })
            throw new InvalidDataException("强袭气息或黑夜穿行之证缺少已核对的材料模板");
        var heroProofItems = HeroProofItemIds.Select(resolve).ToArray();
        if (heroProofItems.Any(item => item.Kind is not (ItemKind.Equipment or ItemKind.Consumable) ||
            item.StackLimit <= 0 || !item.Expiration.SupportsCreation))
            throw new InvalidDataException("英雄之证候选包含不可新建的装备或礼盒");
        var heroProofPool = new DungeonItemDropPool(heroProofItems.Select(item => new DungeonWeightedDropItem(item, 1)));
        hiddenHeroProof = new(3000, heroProofPool, 1, 1, Category: DungeonDropCategory.Equipment)
            { RarityBonusGroup = DungeonRarityBonusGroup.EquipmentLottery };
        normalCertificate = Fixed(certificate, 20, 10);
        normalMaterial = Fixed(material, 50, 25);
        hiddenCertificate = Fixed(certificate, 90, 15);
        hiddenMaterial = Fixed(material, 200, 50);
    }

    public bool Matches(DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        missions.Contains(entry.DungeonId) && !entry.IsTutorial && entry.RouteQuestId == 0 && !entry.IsPractice && entry.HellParty is null &&
        !entry.IsInfernoParty && !entry.IsCosmo && monster.CanReportDeath && !monster.IsFriendly && !monster.IsScriptedBoss &&
        !monster.IsDummyMapBoss && monster.IsBoss && monster.AiCharacter is null && monster.AltarActor is null &&
        monster.HellWave is null && monster.SpawnType <= 3 && monster.SourceMap is { } map && entry.IsTerminalMap(map) &&
        monster.Source is not null && monster.Source.MonsterId == monster.MonsterId && monster.Team >= 100 &&
        monster.ObjectKey != entry.CharacterId;

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        if (!Matches(entry, monster)) return plan;
        var rewards = hiddenMissions.Contains(entry.DungeonId)
            ? new[] { hiddenCertificate, hiddenMaterial, hiddenHeroProof }
            : new[] { normalCertificate, normalMaterial };
        return new(plan.ResourceSource + ";" + Rule, plan.Trials.Concat(rewards), plan.ReferenceQuality, plan.Amplification);
    }

    static DungeonDropTrial Fixed(ItemTemplate template, int total, int stackSize) =>
        new(1000, new DungeonItemDropPool([new DungeonWeightedDropItem(template, 1)]), total, total,
            ProbabilityDenominator: 1000, Category: DungeonDropCategory.Materials, ItemStackSizes: new Dictionary<int, int> { [template.Id] = stackSize }.ToImmutableDictionary());
}
