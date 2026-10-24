using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonAltarPrice(int MinimumLevel, int MaximumLevel, int Gold);
internal sealed record DungeonAltarAdmissionIncrease(int EntryOrdinal, int Percent);

// 旧端价格解析丢弃区间下界、保存上界与金额；本版资源保留完整区间并严格校验。
internal sealed record DungeonAltarAdmissionCatalog(int LottoCost, int Jackpot,
    ImmutableArray<DungeonAltarPrice> Prices, ImmutableArray<DungeonAltarAdmissionIncrease> Increases)
{
    public const string ResourcePath = "etc/(r)serverparameter.etc";
    public static DungeonAltarAdmissionCatalog Load(PvfArchive archive) => Read(archive.ReadTokens(ResourcePath), archive.ResolveString);

    public int BloodFee(int level, int enteredToday)
    {
        if (level is < 1 or > byte.MaxValue || enteredToday < 0) throw new ArgumentOutOfRangeException();
        int price = Prices.FirstOrDefault(row => level <= row.MaximumLevel)?.Gold ?? Prices[^1].Gold;
        int increase = Increases.FirstOrDefault(row => enteredToday < row.EntryOrdinal)?.Percent ?? Increases[^1].Percent;
        return checked(LottoCost + price + (int)((long)increase * price / 100));
    }

    internal static DungeonAltarAdmissionCatalog Read(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var wanted = new HashSet<string>(["[price average]", "[lotto cost]", "[admission increase]", "[jackpot]"]);
        var fields = new Dictionary<string, int[]>();
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3 || !wanted.Contains(resolve(tokens[at].Value))) continue;
            string tag = resolve(tokens[at].Value);
            var values = new List<int>();
            while (++at < tokens.Count && tokens[at].Type != 3)
            {
                if (tokens[at].Type != 0 || tokens[at].Value < 0) throw new InvalidDataException("祭坛价格字段不是非负整数：" + tag);
                values.Add(tokens[at].Value);
            }
            at--;
            if (!fields.TryAdd(tag, values.ToArray())) throw new InvalidDataException("祭坛价格字段重复：" + tag);
        }
        if (fields.Count != wanted.Count || fields["[lotto cost]"] is not [var lotto] || fields["[jackpot]"] is not [var jackpot])
            throw new InvalidDataException("祭坛价格缺少标量或字段");
        var rawPrices = fields["[price average]"];
        var rawIncreases = fields["[admission increase]"];
        if (rawPrices.Length == 0 || rawPrices.Length % 3 != 0 || rawIncreases.Length == 0 || rawIncreases.Length % 2 != 0)
            throw new InvalidDataException("祭坛价格表必须保留完整三整数和两整数行");
        var prices = ImmutableArray.CreateBuilder<DungeonAltarPrice>(rawPrices.Length / 3);
        int previous = 0;
        for (int at = 0; at < rawPrices.Length; at += 3)
        {
            if (rawPrices[at] != previous + 1 || rawPrices[at + 1] < rawPrices[at] || rawPrices[at + 1] > byte.MaxValue)
                throw new InvalidDataException("祭坛价格等级区间不连续");
            prices.Add(new(rawPrices[at], rawPrices[at + 1], rawPrices[at + 2]));
            previous = rawPrices[at + 1];
        }
        var increases = ImmutableArray.CreateBuilder<DungeonAltarAdmissionIncrease>(rawIncreases.Length / 2);
        previous = 0;
        for (int at = 0; at < rawIncreases.Length; at += 2)
        {
            if (rawIncreases[at] <= previous) throw new InvalidDataException("祭坛价格次数阈值不递增");
            increases.Add(new(rawIncreases[at], rawIncreases[at + 1]));
            previous = rawIncreases[at];
        }
        return new(lotto, jackpot, prices.MoveToImmutable(), increases.MoveToImmutable());
    }
}
