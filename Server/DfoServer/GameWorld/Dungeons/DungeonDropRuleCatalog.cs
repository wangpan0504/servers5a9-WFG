using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonDropProbabilityRow(int MinimumLevel, int MaximumLevel, ImmutableArray<int> Values);
internal readonly record struct DungeonDropItemReference(int Level, int Value0, int Value1);
internal readonly record struct DungeonDropGoldReference(int Level, int BaseAmount, int VariancePercent);

// 原整数与single位模式是资源事实；decimal仅供既有参考计算显式使用，不代替原声明。
internal readonly record struct DungeonDropNumber(int RawValue, bool IsFloat)
{
    public double ReferenceDouble => IsFloat ? BitConverter.Int32BitsToSingle(RawValue) : RawValue;

    public decimal ReferenceDecimal
    {
        get
        {
            if (!IsFloat)
                return RawValue;
            try
            {
                return (decimal)BitConverter.Int32BitsToSingle(RawValue);
            }
            catch (OverflowException error)
            {
                throw new NotSupportedException("参考decimal模型无法表示已声明的浮点倍率", error);
            }
        }
    }
}

// 完整保留本版资源轴和全部五列；资源解析不抽奖，不给未知列补业务名称或默认概率。
internal sealed class DungeonDropRuleCatalog
{
    public const string MonsterPath = "etc/itemdropinfo_monseter.etc";
    public const string CommonPath = "etc/itemdropinfo_common.etc";
    public ImmutableArray<DungeonDropProbabilityRow> ProbabilityRows { get; }
    public ImmutableArray<int> RarityDecision { get; }
    public ImmutableArray<DungeonDropNumber> PartyBonus { get; }
    public ImmutableArray<DungeonDropNumber> DifficultyBonus { get; }
    public ImmutableArray<DungeonDropNumber> MonsterTypeBonus { get; }
    public ImmutableArray<int> ConditionRates { get; }
    public ImmutableArray<int> FirstBossNamed { get; }
    public ImmutableArray<int> GoldQuantity { get; }
    public ImmutableArray<int> GoldVolume { get; }
    public ImmutableArray<int> RarityControl { get; }
    public ImmutableArray<DungeonDropItemReference> ItemReferences { get; }
    public ImmutableArray<DungeonDropGoldReference> GoldReferences { get; }

    private DungeonDropRuleCatalog(IReadOnlyList<PvfToken> monster, IReadOnlyList<PvfToken> common, Func<int, string> resolve,
        bool objectRules = false)
    {
        int[] declaredCount = Integers(monster, resolve, "drop prob count", 1);
        int[] probabilities = Integers(monster, resolve, "drop prob");
        if (declaredCount[0] <= 0 || declaredCount[0] > int.MaxValue / 7 || probabilities.Length != declaredCount[0] * 7)
            throw new InvalidDataException("掉落概率行数与完整七列声明不匹配");
        var rows = ImmutableArray.CreateBuilder<DungeonDropProbabilityRow>(declaredCount[0]);
        for (int at = 0; at < probabilities.Length; at += 7)
        {
            int minimum = probabilities[at], maximum = probabilities[at + 1];
            if (minimum <= 0 || maximum < minimum || rows.Count != 0 && minimum <= rows[^1].MaximumLevel ||
                probabilities.AsSpan(at + 2, 5).ContainsAnyExceptInRange(0, int.MaxValue))
                throw new InvalidDataException("掉落概率等级段重叠或含负数概率");
            rows.Add(new(minimum, maximum, probabilities.AsSpan(at + 2, 5).ToArray().ToImmutableArray()));
        }
        ProbabilityRows = rows.MoveToImmutable();
        int[] rarity = Integers(monster, resolve, "basis of rarity dicision", 28);
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 7; column++)
        {
            int at = row * 7 + column;
            if (rarity[at] < 0 || column != 0 && rarity[at] < rarity[at - 1])
                throw new InvalidDataException("掉落稀有度阈值必须按原序非递减");
        }
        // 官方尾档1000001/1000002/1000003在百万骰之外；归一后稀有度骰、史诗掉落Buff与GM掉落控制使用同一份可达区间。
        RarityDecision = DungeonRarityThresholds.Normalize(rarity);
        PartyBonus = objectRules ? [] : Numbers(monster, resolve, "party member drop bonusrate", 20);
        DifficultyBonus = Numbers(monster, resolve, "dungeon difficulty drop bonusrate", 25);
        MonsterTypeBonus = Numbers(monster, resolve, "monster type drop bonusrate", 20);
        ConditionRates = objectRules ? [] : Integers(monster, resolve, "condition rate", 8).ToImmutableArray();
        FirstBossNamed = objectRules ? [] : Integers(monster, resolve, "first boss/named mob hunting", 2).ToImmutableArray();
        GoldQuantity = objectRules ? [] : Integers(monster, resolve, "gold quantity", 4).ToImmutableArray();
        GoldVolume = objectRules ? [] : Integers(monster, resolve, "gold volume", 2).ToImmutableArray();
        RarityControl = Integers(monster, resolve, "item drop rarity control", objectRules ? 5 : 10).ToImmutableArray();

        int[] items = Integers(monster, resolve, "item drop ref table");
        if (items.Length == 0 || items.Length % 3 != 0)
            throw new InvalidDataException("掉落物品引用表必须是完整三列");
        var itemRows = ImmutableArray.CreateBuilder<DungeonDropItemReference>(items.Length / 3);
        var levels = new HashSet<int>();
        for (int at = 0; at < items.Length; at += 3)
        {
            if (items[at] <= 0 || !levels.Add(items[at]))
                throw new InvalidDataException("掉落物品引用等级无效或重复");
            itemRows.Add(new(items[at], items[at + 1], items[at + 2]));
        }
        ItemReferences = itemRows.MoveToImmutable();

        int[] gold = Integers(common, resolve, "gold drop ref table");
        if (gold.Length == 0 || gold.Length % 3 != 0)
            throw new InvalidDataException("金币引用表必须是完整等级、基值与波动三列");
        var goldRows = ImmutableArray.CreateBuilder<DungeonDropGoldReference>(gold.Length / 3);
        levels.Clear();
        for (int at = 0; at < gold.Length; at += 3)
        {
            if (gold[at] <= 0 || !levels.Add(gold[at]) || gold[at + 1] <= 0 || gold[at + 2] is < 0 or > 100)
                throw new InvalidDataException("金币引用等级、基值或波动范围无效");
            goldRows.Add(new(gold[at], gold[at + 1], gold[at + 2]));
        }
        GoldReferences = goldRows.MoveToImmutable();
    }

    public static DungeonDropRuleCatalog Load(PvfArchive archive) => Parse(
        archive.ReadTokens(MonsterPath), archive.ReadTokens(CommonPath), archive.ResolveString);

    public static DungeonDropRuleCatalog LoadObjects(PvfArchive archive) => new(
        archive.ReadTokens("etc/itemdropinfo_object.etc"), archive.ReadTokens(CommonPath), archive.ResolveString, objectRules: true);

    internal static DungeonDropRuleCatalog Parse(IReadOnlyList<PvfToken> monster, IReadOnlyList<PvfToken> common,
        Func<int, string> resolve, bool objectRules = false) => new(monster, common, resolve, objectRules);

    static PvfToken[] Field(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, string name)
    {
        PvfToken[]? result = null;
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3 || resolve(tokens[at].Value).ToLowerInvariant() != "[" + name + "]")
                continue;
            if (result is not null)
                throw new InvalidDataException("重复掉落资源字段: " + name);
            int begin = ++at;
            while (at < tokens.Count && tokens[at].Type != 3)
                at++;
            result = Enumerable.Range(begin, at - begin).Select(index => tokens[index]).ToArray();
            at--;
        }
        return result ?? throw new InvalidDataException("缺少掉落资源字段: " + name);
    }

    static int[] Integers(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, string name, int? count = null)
    {
        var field = Field(tokens, resolve, name);
        if (count is not null && field.Length != count || field.Any(token => token.Type != 0))
            throw new InvalidDataException("掉落资源整数宽度或类型不符: " + name);
        return field.Select(token => token.Value).ToArray();
    }

    static ImmutableArray<DungeonDropNumber> Numbers(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, string name, int count)
    {
        var field = Field(tokens, resolve, name);
        if (field.Length != count)
            throw new InvalidDataException("掉落倍率矩阵宽度不符: " + name);
        var result = ImmutableArray.CreateBuilder<DungeonDropNumber>(count);
        foreach (var token in field)
        {
            if (token.Type == 0)
            {
                if (token.Value < 0)
                    throw new InvalidDataException("掉落倍率不是非负数字: " + name);
                result.Add(new(token.Value, false));
                continue;
            }
            float value = token.Type == 2 ? BitConverter.Int32BitsToSingle(token.Value) : float.NaN;
            if (!float.IsFinite(value) || value < 0)
                throw new InvalidDataException("掉落倍率不是非负有限数字: " + name);
            result.Add(new(token.Value, true));
        }
        return result.MoveToImmutable();
    }
}
