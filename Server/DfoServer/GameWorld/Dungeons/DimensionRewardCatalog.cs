using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed class DimensionRewardCatalog
{
    public const string PoolPath = "etc/dimensiongatedroplist.etc";
    public const string ExcludePath = "etc/dimensiongatedroplist_exclude.etc";
    public ImmutableDictionary<(int Job, int Grow), (ImmutableArray<int> Normal, ImmutableArray<int> Set)> Professions { get; }
    public ImmutableArray<int> Legendary { get; }
    public ImmutableDictionary<int, (int RewardPercent, int SetPercent)> CardRates { get; }
    public ImmutableDictionary<int, (int NormalBox, int BonusBox)> AncientBoxes { get; }

    public DimensionRewardCatalog(PvfArchive pvf, ItemCatalog items)
    {
        var excludes = new PvfTokenDocument(pvf.ReadTokens(ExcludePath), pvf.ResolveString);
        var excluded = Integers(excludes.Sections.Single(row => row.Name == "exclude list")).ToHashSet();
        var document = new PvfTokenDocument(pvf.ReadTokens(PoolPath), pvf.ResolveString);
        if (!document.Diagnostics.IsEmpty || !excludes.Diagnostics.IsEmpty)
            throw new InvalidDataException("异界装备名册结构无效");
        var professions = ImmutableDictionary.CreateBuilder<(int, int), (ImmutableArray<int>, ImmutableArray<int>)>();
        foreach (var row in document.Sections.Where(row => row.Name == "chronicle grow type"))
        {
            var profession = Integers(row);
            if (profession is not [>= 0 and <= 15, >= 0 and <= 15]) throw new InvalidDataException("异界职业行头无效");
            ImmutableArray<int> Pool(string name) => [.. Integers(row.Children.Single(child => child.Name == name))
                .Where(id => !excluded.Contains(id)).Distinct()];
            if (!professions.TryAdd((profession[0], profession[1]), (Pool("normal chronicle list"), Pool("set chronicle list"))))
                throw new InvalidDataException("异界职业重复登记");
        }
        Professions = professions.ToImmutable();
        Legendary = [.. Integers(document.Sections.Single(row => row.Name == "legendary group")).Where(id => !excluded.Contains(id)).Distinct()];
        var clear = new PvfTokenDocument(pvf.ReadTokens(ClearRewardSections.Path), pvf.ResolveString);
        var rates = Integers(clear.Sections.Single(row => row.Name == "chronicle set reward rate"));
        if (rates.Length != 9) throw new InvalidDataException("异界翻牌概率必须完整声明三档");
        CardRates = Enumerable.Range(0, 3).ToImmutableDictionary(index => rates[index * 3], index => (rates[index * 3 + 1], rates[index * 3 + 2]));
        if (CardRates.Keys.Order().SequenceEqual([0, 1, 2]) == false || CardRates.Values.Any(rate => rate.Item1 is < 0 or > 100 || rate.Item2 is < 0 or > 100))
            throw new InvalidDataException("异界翻牌概率越界");
        var ancient = new PvfTokenDocument(pvf.ReadTokens("etc/ancientgate.etc"), pvf.ResolveString);
        if (Integers(ancient.Sections.Single(row => row.Name == "max value")) is not [10000] ||
            Integers(ancient.Sections.Single(row => row.Name == "base rising value")) is not [5])
            throw new NotSupportedException("远古能量资源超出当前本服兼容合同");
        var boxes = Integers(ancient.Sections.Single(row => row.Name == "clear reward"));
        if (boxes.Length != 21) throw new InvalidDataException("远古奖励须完整声明七图");
        AncientBoxes = Enumerable.Range(0, 7).ToImmutableDictionary(index => boxes[index * 3], index => (boxes[index * 3 + 1], boxes[index * 3 + 2]));
        foreach (int id in Professions.Values.SelectMany(row => row.Normal.Concat(row.Set)).Concat(Legendary).Distinct())
            if (items.Get(id) is not { Kind: ItemKind.Equipment, Expiration.SupportsCreation: true })
                throw new InvalidDataException("异界奖池不是可创建装备：" + id);
        foreach (var pair in AncientBoxes.Values)
            foreach (int id in new[] { pair.NormalBox, pair.BonusBox })
                if (items.Get(id) is not { Kind: ItemKind.Consumable, Expiration.SupportsCreation: true })
                    throw new InvalidDataException("远古箱不是可创建物品：" + id);
    }

    static int[] Integers(PvfTokenSection row)
    {
        if (row.Values.Any(value => value.Type != 0)) throw new InvalidDataException("异界奖励字段不是整数：" + row.Name);
        return [.. row.Values.Select(value => value.Value)];
    }
}
