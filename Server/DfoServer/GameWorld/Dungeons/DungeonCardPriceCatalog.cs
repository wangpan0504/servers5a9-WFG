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
            TayberrsPaidRewards = DungeonSpecificCardRewards.ParseTayberrsPaid(tokens, resolve)
        };
    }

    public int Price(int level)
    {
        if (level <= 0) throw new InvalidDataException("付费翻牌需要副本基准等级");
        return rows.LastOrDefault(row => row.Level <= level,rows[0]).Price;
    }
}
