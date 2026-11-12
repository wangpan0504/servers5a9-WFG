using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record SecretShopLevelSection(int Index, int Minimum, int Maximum);
internal sealed record SecretShopAppearance(int Key, int Merchant, ImmutableArray<int> Weights);
internal sealed record SecretShopProduct(int ItemId, int Option, int Gold, int Premium, int Count, int Weight);
internal sealed record SecretShopPool(int Slots, ImmutableArray<SecretShopProduct> Products);
internal sealed record SecretShopMerchant(int Kind,
    FrozenDictionary<int, SecretShopPool> Levels, FrozenDictionary<int, SecretShopPool> CashLevels,
    FrozenDictionary<int, SecretShopPool> Dungeons, FrozenDictionary<int, SecretShopPool> CashDungeons);

// 神秘商店与NPC固定商店使用不同的出现表、商品池和报价，不从STK普通售价反推。
internal sealed class SecretShopCatalog
{
    public const string Path = "etc/secretshop.etc";
    public const int AbsentMerchant = 1000;
    public int WaitSeconds { get; }
    public int PriceRandom { get; }
    public int CashStackableCount { get; }
    public int ExtraCashStackableCount { get; }
    public ImmutableArray<SecretShopLevelSection> Sections { get; }
    public ImmutableArray<SecretShopAppearance> DungeonAppearance { get; }
    public ImmutableArray<SecretShopAppearance> LevelAppearance { get; }
    public FrozenDictionary<int, SecretShopMerchant> Merchants { get; }

    public SecretShopCatalog(PvfArchive archive) : this(archive.ReadTokens(Path), archive.ResolveString) { }

    internal SecretShopCatalog(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        // 匹配资源的12个[dungeon]池以[/dungeon item]闭合；仅接受这一处已知别名。
        var document = new PvfTokenDocument(tokens, resolve,
            new Dictionary<string, string> { ["dungeon"] = "dungeon item" });
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("神秘商店配置标签未完整配对");
        int[] Values(PvfTokenSection section)
        {
            if (section.Values.Any(token => token.Type != 0))
                throw new InvalidDataException("神秘商店配置必须使用整数: " + section.Name);
            return section.Values.Select(token => token.Value).ToArray();
        }
        PvfTokenSection Single(string name) => document.Sections.Single(section => section.Name == name);
        int Scalar(string name)
        {
            var section = Single(name);
            if (Values(section) is not [var value] || !section.Children.IsEmpty)
                throw new InvalidDataException("神秘商店标量无效: " + name);
            return value;
        }
        WaitSeconds = Scalar("eplp wait time");
        PriceRandom = Scalar("price random");
        CashStackableCount = Scalar("cash user stackable count");
        ExtraCashStackableCount = Scalar("add slot cash user stackable count");
        if (WaitSeconds is < 0 or > 3600 || PriceRandom is < 0 or > 100 ||
            CashStackableCount < 0 || ExtraCashStackableCount < 0)
            throw new InvalidDataException("神秘商店等待、价格或契约数量越界");

        var levels = Values(Single("level section"));
        if (levels.Length == 0 || levels.Length % 3 != 0)
            throw new InvalidDataException("神秘商店等级区间不是完整三元组");
        Sections = Enumerable.Range(0, levels.Length / 3).Select(index =>
            new SecretShopLevelSection(levels[index * 3], levels[index * 3 + 1], levels[index * 3 + 2])).ToImmutableArray();
        if (Sections.Any(row => row.Index < 0 || row.Minimum < 1 || row.Maximum < row.Minimum) ||
            Sections.Select(row => row.Index).Distinct().Count() != Sections.Length ||
            Sections.Any(row => Sections.Any(other => !ReferenceEquals(row, other) &&
                row.Minimum <= other.Maximum && other.Minimum <= row.Maximum)))
            throw new InvalidDataException("神秘商店等级区间重复、重叠或越界");
        DungeonAppearance = Appearance(Single("dungeon npc"));
        LevelAppearance = Appearance(Single("level npc"));
        var merchants = new Dictionary<int, SecretShopMerchant>();
        foreach (var section in document.Sections.Where(section => section.Name == "npc"))
        {
            if (!section.IsBlock || Values(section) is not [> 0 and var kind] || kind == AbsentMerchant)
                throw new InvalidDataException("神秘商店商人编号无效");
            var merchant = new SecretShopMerchant(kind, Pools(section, "level"), Pools(section, "cash item"),
                Pools(section, "dungeon"), Pools(section, "dungeon cash item"));
            if (!merchants.TryAdd(kind, merchant)) throw new InvalidDataException("神秘商店商人重复");
        }
        Merchants = merchants.ToFrozenDictionary();
        if (Merchants.Count == 0 || DungeonAppearance.Concat(LevelAppearance)
            .Any(row => row.Merchant != AbsentMerchant && !Merchants.ContainsKey(row.Merchant)) ||
            LevelAppearance.Any(row => !Sections.Any(section => section.Index == row.Key)))
            throw new InvalidDataException("神秘商店出现表引用了未声明的商人或等级区间");

        ImmutableArray<SecretShopAppearance> Appearance(PvfTokenSection section)
        {
            int[] values = Values(section);
            if (!section.IsBlock || !section.Children.IsEmpty || values.Length % 7 != 0)
                throw new InvalidDataException("神秘商店出现表不是完整七元组");
            var rows = Enumerable.Range(0, values.Length / 7).Select(index => new SecretShopAppearance(
                values[index * 7], values[index * 7 + 1], values.AsSpan(index * 7 + 2, 5).ToArray().ToImmutableArray())).ToImmutableArray();
            if (rows.Any(row => row.Key < 0 || row.Merchant <= 0 || row.Weights.Any(weight => weight is < 0 or > 10000)) ||
                rows.Select(row => (row.Key, row.Merchant)).Distinct().Count() != rows.Length ||
                rows.GroupBy(row => row.Key).Any(group => Enumerable.Range(0, 5).Any(difficulty =>
                    group.Sum(row => (long)row.Weights[difficulty]) != 10000)))
                throw new InvalidDataException("神秘商店出现表重复或各难度权重未覆盖万分骰");
            return rows;
        }

        FrozenDictionary<int, SecretShopPool> Pools(PvfTokenSection merchant, string name)
        {
            var pools = new Dictionary<int, SecretShopPool>();
            foreach (var section in merchant.Children.Where(section => section.Name == name))
            {
                int[] values = Values(section);
                if (!section.IsBlock || !section.Children.IsEmpty || values.Length < 2 || (values.Length - 2) % 6 != 0 ||
                    values[0] < 0 || values[1] is < 0 or > 255)
                    throw new InvalidDataException("神秘商店商品池头或六元组无效");
                var products = Enumerable.Range(0, (values.Length - 2) / 6).Select(index =>
                {
                    int at = 2 + index * 6;
                    return new SecretShopProduct(values[at], values[at + 1], values[at + 2], values[at + 3], values[at + 4], values[at + 5]);
                }).ToImmutableArray();
                if (products.Any(row => row.ItemId <= 0 || row.Option is < 0 or > 255 || row.Gold < 0 || row.Premium < 0 || row.Count <= 0 || row.Weight < 0) ||
                    !pools.TryAdd(values[0], new(values[1], products)))
                    throw new InvalidDataException("神秘商店商品池重复或商品数量、权重、费用无效");
            }
            return pools.ToFrozenDictionary();
        }
    }
}
