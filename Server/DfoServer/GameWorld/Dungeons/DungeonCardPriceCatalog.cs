using PvfLib;
using System.Collections.Immutable;

namespace DfoServer.GameWorld.Dungeons;

internal sealed class DungeonCardPriceCatalog
{
    public const string ResourcePath = "etc/itemdropinfo_clearreward.etc";
    readonly (int Level, int Price)[] rows;
    DungeonCardPriceCatalog((int Level, int Price)[] rows) => this.rows = rows;
    public ImmutableDictionary<int, ImmutableArray<DungeonSpecificCardItem>> SpecificRewards { get; private init; } =
        ImmutableDictionary<int, ImmutableArray<DungeonSpecificCardItem>>.Empty;

    public ImmutableArray<DungeonSpecificCardItem> TayberrsPaidRewards { get; private init; } = [];
    public ImmutableDictionary<int, SpecialDungeonClearReward> SpecialClearRewards { get; private init; } =
        ImmutableDictionary<int, SpecialDungeonClearReward>.Empty;
    public ImmutableArray<int> RarityDecision { get; private init; } = [];
    public ImmutableArray<DungeonDropItemReference> ItemReferences { get; private init; } = [];

    public static DungeonCardPriceCatalog Load(PvfArchive archive) => Parse(archive.ReadTokens(ResourcePath), archive.ResolveString);

    internal static DungeonCardPriceCatalog Parse(IReadOnlyList<PvfToken> tokens, Func<int,string> resolve)
    {
        PvfToken[]? values = null;
        for (int index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Type != 3 || !resolve(tokens[index].Value).Equals("[gold card cost table]",StringComparison.OrdinalIgnoreCase)) continue;
            if (values is not null) throw new InvalidDataException("付费翻牌价格表重复");
            int start = ++index;
            while (index < tokens.Count && tokens[index].Type != 3) index++;
            values = Enumerable.Range(start,index-start).Select(at => tokens[at]).ToArray();
            index--;
        }
        if (values is null || values.Length == 0 || values.Length % 2 != 0)
            throw new InvalidDataException("付费翻牌价格表需要完整且唯一的等级/金币整数对");
        var result = new List<(int Level,int Price)>();
        var levels = new HashSet<int>();
        for (int at = 0; at < values.Length; at += 2)
        {
            if (values[at] is not { Type:0,Value:>0 } || values[at+1] is not { Type:0,Value:>=0 } || !levels.Add(values[at].Value))
                throw new InvalidDataException("付费翻牌等级重复或价格不是非负整数");
            result.Add((values[at].Value,values[at+1].Value));
        }
        return new(result.OrderBy(row => row.Level).ToArray())
        {
            SpecificRewards = DungeonSpecificCardRewards.Parse(tokens, resolve),
            TayberrsPaidRewards = DungeonSpecificCardRewards.ParseTayberrsPaid(tokens, resolve),
            SpecialClearRewards = DungeonSpecificCardRewards.ParseSpecialClearRewards(tokens, resolve),
            RarityDecision = ReadRarity(),
            ItemReferences = ReadReferences()
        };

        int[]? ReadIntegers(string tag)
        {
            int[]? found = null;
            for (int index = 0; index < tokens.Count; index++)
            {
                if (tokens[index].Type != 3 || !resolve(tokens[index].Value).Equals("[" + tag + "]", StringComparison.OrdinalIgnoreCase)) continue;
                if (found is not null) throw new InvalidDataException("翻牌资源字段重复: " + tag);
                var data = new List<int>();
                while (++index < tokens.Count && tokens[index].Type != 3)
                {
                    if (tokens[index].Type != 0 || tokens[index].Value < 0)
                        throw new InvalidDataException("翻牌资源字段须为非负整数: " + tag);
                    data.Add(tokens[index].Value);
                }
                index--;
                found = data.ToArray();
            }
            return found;
        }
        ImmutableArray<int> ReadRarity()
        {
            // 只读价格的旧输入仍合法；存在的生成声明必须完整，不能静默改用怪物阈值。
            if (ReadIntegers("basis of rarity dicision") is not { } data) return [];
            if (data.Length != 7 || data[^1] < DungeonRarityThresholds.Denominator || !data.SequenceEqual(data.Order()))
                throw new InvalidDataException("翻牌稀有度须提供完整七档非递减阈值");
            return DungeonRarityThresholds.Normalize(data);
        }
        ImmutableArray<DungeonDropItemReference> ReadReferences()
        {
            if (ReadIntegers("item drop ref table") is not { } data) return [];
            if (data.Length == 0 || data.Length % 3 != 0)
                throw new InvalidDataException("翻牌等级引用须为完整三列");
            var references = ImmutableArray.CreateBuilder<DungeonDropItemReference>(data.Length / 3);
            var seen = new HashSet<int>();
            for (int at = 0; at < data.Length; at += 3)
            {
                if (data[at] == 0 || !seen.Add(data[at])) throw new InvalidDataException("翻牌引用等级无效或重复");
                references.Add(new(data[at], data[at + 1], data[at + 2]));
            }
            return references.MoveToImmutable();
        }
    }

    public int Price(int level)
    {
        if (level <= 0) throw new InvalidDataException("付费翻牌需要副本基准等级");
        return rows.LastOrDefault(row => row.Level <= level,rows[0]).Price;
    }
}
