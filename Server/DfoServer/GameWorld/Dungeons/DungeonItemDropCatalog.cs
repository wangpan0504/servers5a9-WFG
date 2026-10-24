using System.Collections.Immutable;
using DfoServer.GameWorld.Items;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonItemDropCandidate(ItemTemplate Template, int CumulativeWeight);
internal sealed record DungeonWeightedDropItem(ItemTemplate Template, int Weight);

internal sealed class DungeonItemDropPool
{
    public ImmutableArray<DungeonItemDropCandidate> Entries { get; }
    public int TotalWeight { get; }
    internal DungeonItemDropPool(IEnumerable<ItemTemplate> templates)
        : this(templates.Select(template => new DungeonWeightedDropItem(template, template.CreationRate))) { }

    internal DungeonItemDropPool(IEnumerable<DungeonWeightedDropItem> templates)
    {
        var result = ImmutableArray.CreateBuilder<DungeonItemDropCandidate>();
        int total = 0;
        foreach (var entry in templates)
        {
            if (entry.Weight <= 0)
                throw new InvalidDataException("掉落池候选必须保留声明的正权重");
            total = checked(total + entry.Weight);
            result.Add(new(entry.Template, total));
        }
        Entries = result.ToImmutable();
        TotalWeight = total;
    }
    public ItemTemplate Select(int roll)
    {
        if (roll < 0 || roll >= TotalWeight)
            throw new ArgumentOutOfRangeException(nameof(roll));
        foreach (var entry in Entries)
            if (roll < entry.CumulativeWeight)
                return entry.Template;
        throw new InvalidOperationException("已验证掉落池缺少选择区间");
    }
}

// 模板目录在监听前共享装配；按类别/grade/rarity查询，不为每次死亡扫描全部物品。
internal sealed class DungeonItemDropCatalog
{
    readonly IReadOnlyDictionary<(ItemKind Kind, int Grade, int Rarity), ImmutableArray<ItemTemplate>> buckets;
    private DungeonItemDropCatalog(IEnumerable<ItemTemplate> source)
    {
        var templates = source.ToArray();
        if (templates.Select(template => template.Id).Distinct().Count() != templates.Length ||
            templates.Any(template => template.CreationRate <= 0 || template.Grade <= 0 || template.Rarity is < 0 or > 6))
            throw new InvalidDataException("资源掉落候选模板重复或grade/rarity/权重非法");
        buckets = templates.GroupBy(template => (template.Kind, template.Grade, template.Rarity)).ToDictionary(
            group => group.Key, group => group.OrderBy(template => template.Id).ToImmutableArray());
    }

    public static DungeonItemDropCatalog Load(ItemCatalog items) => new(items.DropCandidates());
    public static DungeonItemDropCatalog LoadAltar(ItemCatalog items) => new(items.DropCandidates(includeSpecialEquipment: true).Where(template => template.Grade > 0));
    internal static DungeonItemDropCatalog Create(IEnumerable<ItemTemplate> templates) => new(templates);

    // 历史_itemTypeVerify：2接收非堆叠装备，4只接收宠物及宠物装备；空池保持空，不回退稀有度或等级。
    public DungeonItemDropPool AltarPool(bool creaturesOnly, int minimumGrade, int maximumGrade, int rarity)
    {
        if (rarity is < 0 or > 6 || minimumGrade > maximumGrade) throw new ArgumentOutOfRangeException(nameof(rarity));
        var result = new List<ItemTemplate>();
        ItemKind[] kinds = creaturesOnly ? [ItemKind.Creature, ItemKind.CreatureEquipment] :
            [ItemKind.Equipment, ItemKind.Avatar, ItemKind.Creature, ItemKind.CreatureEquipment];
        for (int grade = Math.Max(1, minimumGrade); grade <= Math.Min(200, maximumGrade); grade++)
        foreach (var kind in kinds)
            if (buckets.TryGetValue((kind, grade, rarity), out var entries)) result.AddRange(entries);
        return new(result);
    }

    public DungeonItemDropPool Pool(ItemKind kind, int minimumGrade, int maximumGrade, int rarity)
    {
        if (minimumGrade <= 0 || maximumGrade < minimumGrade || maximumGrade > 200 || rarity is < 0 or > 6)
            throw new ArgumentOutOfRangeException(nameof(minimumGrade));
        var result = new List<ItemTemplate>();
        for (int grade = minimumGrade; grade <= maximumGrade; grade++)
            if (buckets.TryGetValue((kind, grade, rarity), out var entries))
                result.AddRange(entries);
        return new(result);
    }

    // 本服参考规则：堆叠品等权、装备按当前脚本creation rate；空池可回退同类rarity0。
    // 本版(R)ItemDictionary布局与90参考不同，不把其展示列当作装备生成权重。
    public DungeonItemDropPool ReferencePool(bool equipment, int minimumGrade, int maximumGrade, int rarity)
    {
        if (rarity is < 0 or > 6)
            throw new ArgumentOutOfRangeException(nameof(rarity));
        minimumGrade = Math.Max(1, minimumGrade);
        maximumGrade = Math.Min(200, maximumGrade);
        var candidates = Candidates(minimumGrade, maximumGrade, rarity);
        if (candidates.Count == 0 && rarity > 0)
            candidates = Candidates(minimumGrade, maximumGrade, 0);
        if (candidates.Count == 0 && equipment)
        {
            candidates = Nearest(rarity);
            if (candidates.Count == 0 && rarity > 0)
                candidates = Nearest(0);
        }
        return new(candidates.Select(template => new DungeonWeightedDropItem(template, equipment ? template.CreationRate : 1)));

        List<ItemTemplate> Candidates(int minimum, int maximum, int targetRarity)
        {
            var result = new List<ItemTemplate>();
            for (int grade = minimum; grade <= maximum; grade++)
            foreach (var kind in equipment ? EquipmentKinds : StackableKinds)
                if (buckets.TryGetValue((kind, grade, targetRarity), out var entries))
                    result.AddRange(entries);
            return result;
        }
        List<ItemTemplate> Nearest(int targetRarity)
        {
            for (int grade = Math.Min(200, minimumGrade - 1); grade >= 1; grade--)
                if (buckets.TryGetValue((ItemKind.Equipment, grade, targetRarity), out var entries))
                    return entries.ToList();
            return [];
        }
    }

    static ReadOnlySpan<ItemKind> EquipmentKinds => [ItemKind.Equipment];
    static ReadOnlySpan<ItemKind> StackableKinds => [ItemKind.Consumable, ItemKind.Material, ItemKind.Profession];
}
