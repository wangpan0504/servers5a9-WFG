using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal readonly record struct CrackItemReward(int ItemId, int Count);

internal sealed record CrackLevelBand(int MinLevel, int MaxLevel, ImmutableArray<CrackItemReward> Items);

internal readonly record struct CrackDailyReward(int Clears, int ItemId, int Count);

internal sealed record CrackDimensionDecision(ImmutableArray<CrackItemReward> ClearItems, CrackItemReward? Daily, int ClearsAfter, bool DailyGranted)
{
    public ImmutableArray<CrackItemReward> Granted => Daily is { } daily ? ClearItems.Add(daily) : ClearItems;
}

// etc/crackofdimensionlist.etc：等级段 [reward int data] 是通关物品和数量，
// [daily reward info] 是「当日通关次数、物品、数量」。发放走结算时的既有物品入库，不进入死亡或翻牌包。
internal sealed class CrackDimensionCatalog
{
    public const string Path = "etc/crackofdimensionlist.etc";

    CrackDimensionCatalog(int limitMinLevel, int dailyEnterCount, int dailyClearCount, CrackDailyReward daily,
        ImmutableArray<CrackLevelBand> bands)
    {
        LimitMinLevel = limitMinLevel;
        DailyEnterCount = dailyEnterCount;
        DailyClearCount = dailyClearCount;
        Daily = daily;
        Bands = bands;
    }

    public int LimitMinLevel { get; }
    public int DailyEnterCount { get; }
    public int DailyClearCount { get; }
    public CrackDailyReward Daily { get; }
    public ImmutableArray<CrackLevelBand> Bands { get; }

    public static CrackDimensionCatalog Load(PvfArchive archive, Func<int, bool> isItem) =>
        Parse(archive.ReadTokens(Path), archive.ResolveString, isItem);

    public static CrackDimensionCatalog Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, Func<int, bool> isItem)
    {
        int limit = -1, enter = -1, clears = -1;
        CrackDailyReward? daily = null;
        var bands = ImmutableArray.CreateBuilder<CrackLevelBand>();
        bool buff = false, info = false;
        int at = 0;
        while (at < tokens.Count)
        {
            string tag = TagAt(tokens, resolve, at);
            switch (tag)
            {
                case "[limit min level]":
                    if (limit >= 0) throw new InvalidDataException("次元裂缝最低等级重复");
                    limit = One(tokens, resolve, ref at, "limit min level");
                    break;
                case "[daily enter count]":
                    if (enter >= 0) throw new InvalidDataException("次元裂缝每日入场次数重复");
                    enter = One(tokens, resolve, ref at, "daily enter count");
                    break;
                case "[daily clear count]":
                    if (clears >= 0) throw new InvalidDataException("次元裂缝每日通关次数重复");
                    clears = One(tokens, resolve, ref at, "daily clear count");
                    break;
                case "[daily reward info]":
                    if (daily is not null) throw new InvalidDataException("次元裂缝每日奖励重复");
                    var row = Ints(tokens, resolve, ref at, "[daily reward info]", "[/daily reward info]");
                    if (row.Length != 3) throw new InvalidDataException("次元裂缝每日奖励必须是次数、物品、数量");
                    daily = new(row[0], Item(row[1], isItem), Amount(row[2]));
                    break;
                case "[reward data]":
                    bands.Add(Band(tokens, resolve, isItem, ref at));
                    break;
                case "[crack buff list]":
                    if (buff) throw new InvalidDataException("次元裂缝 buff 列表重复");
                    Ints(tokens, resolve, ref at, "[crack buff list]", "[/crack buff list]");
                    buff = true;
                    break;
                case "[crack info list]":
                    if (info) throw new InvalidDataException("次元裂缝信息列表重复");
                    Ints(tokens, resolve, ref at, "[crack info list]", "[/crack info list]");
                    info = true;
                    break;
                default:
                    throw new InvalidDataException("次元裂缝出现未识别字段 " + tag);
            }
        }
        if (limit < 1 || enter < 1 || clears < 1 || daily is null || bands.Count == 0 || !buff || !info)
            throw new InvalidDataException("次元裂缝奖励段缺失");
        if (daily.Value.Clears != clears)
            throw new InvalidDataException("次元裂缝每日奖励次数与每日通关次数不一致");
        var ordered = bands.OrderBy(band => band.MinLevel).ToImmutableArray();
        for (int i = 1; i < ordered.Length; i++)
            if (ordered[i].MinLevel <= ordered[i - 1].MaxLevel)
                throw new InvalidDataException("次元裂缝奖励等级段重叠");
        return new(limit, enter, clears, daily.Value, ordered);
    }

    public CrackDimensionDecision Decide(int level, int clearsBefore, bool dailyGranted)
    {
        if (clearsBefore < 0) throw new InvalidDataException("次元裂缝当日通关次数无效");
        int next = checked(clearsBefore + 1);
        var clear = level >= LimitMinLevel ? ClearItems(level) : ImmutableArray<CrackItemReward>.Empty;
        CrackItemReward? dailyItem = !dailyGranted && next >= Daily.Clears ? new CrackItemReward(Daily.ItemId, Daily.Count) : null;
        return new(clear, dailyItem, next, dailyGranted || dailyItem is not null);
    }

    ImmutableArray<CrackItemReward> ClearItems(int level)
    {
        foreach (var band in Bands)
            if (level >= band.MinLevel && level <= band.MaxLevel)
                return band.Items;
        return ImmutableArray<CrackItemReward>.Empty;
    }

    static CrackLevelBand Band(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, Func<int, bool> isItem, ref int at)
    {
        at++;
        int? min = null, max = null;
        ImmutableArray<CrackItemReward>? items = null;
        while (at < tokens.Count && TagAt(tokens, resolve, at) != "[/reward data]")
        {
            string tag = TagAt(tokens, resolve, at);
            if (tag == "[min level]") min = Assign(min, OneOpen(tokens, ref at), "次元裂缝奖励最低等级重复");
            else if (tag == "[max level]") max = Assign(max, OneOpen(tokens, ref at), "次元裂缝奖励最高等级重复");
            else if (tag == "[reward int data]")
            {
                if (items is not null) throw new InvalidDataException("次元裂缝通关奖励重复");
                var row = Ints(tokens, resolve, ref at, "[reward int data]", "[/reward int data]");
                if (row.Length == 0 || row.Length % 2 != 0)
                    throw new InvalidDataException("次元裂缝通关奖励必须成对的物品和数量");
                var built = ImmutableArray.CreateBuilder<CrackItemReward>(row.Length / 2);
                for (int i = 0; i < row.Length; i += 2)
                    built.Add(new(Item(row[i], isItem), Amount(row[i + 1])));
                items = built.DrainToImmutable();
            }
            else throw new InvalidDataException("次元裂缝奖励段出现未识别字段 " + tag);
        }
        if (at >= tokens.Count || min is null || max is null || items is null || min > max || min < 1)
            throw new InvalidDataException("次元裂缝奖励等级段无效");
        at++;
        return new(min.Value, max.Value, items.Value);
    }

    static int Assign(int? current, int value, string error)
    {
        if (current is not null) throw new InvalidDataException(error);
        return value;
    }

    static int One(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, ref int at, string name)
    {
        var values = Ints(tokens, resolve, ref at, "[" + name + "]", "[/" + name + "]");
        if (values.Length != 1 || values[0] < 1) throw new InvalidDataException("次元裂缝 " + name + " 无效");
        return values[0];
    }

    static int OneOpen(IReadOnlyList<PvfToken> tokens, ref int at)
    {
        if (at + 1 >= tokens.Count || tokens[at + 1].Type != 0 || tokens[at + 1].Value < 1)
            throw new InvalidDataException("次元裂缝等级无效");
        int value = tokens[at + 1].Value;
        at += 2;
        return value;
    }

    static int[] Ints(IReadOnlyList<PvfToken> tokens, Func<int, string>? resolve, ref int at, string open, string close)
    {
        if (resolve is not null && Tag(tokens, resolve, at) != open)
            throw new InvalidDataException("次元裂缝字段缺失 " + open);
        at++;
        var values = new List<int>();
        while (at < tokens.Count && tokens[at].Type != 3)
        {
            if (tokens[at].Type != 0) throw new InvalidDataException("次元裂缝奖励段含非整数");
            values.Add(tokens[at].Value);
            at++;
        }
        if (at >= tokens.Count || resolve is not null && Tag(tokens, resolve, at) != close)
            throw new InvalidDataException("次元裂缝字段未闭合 " + open);
        at++;
        return values.ToArray();
    }

    static string TagAt(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, int at) => Tag(tokens, resolve, at);

    static string Tag(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, int at)
    {
        if (at >= tokens.Count || tokens[at].Type != 3)
            throw new InvalidDataException("次元裂缝字段不是标签");
        return resolve(tokens[at].Value).Trim().ToLowerInvariant();
    }

    static int Item(int id, Func<int, bool> isItem)
    {
        if (id <= 0 || !isItem(id)) throw new InvalidDataException("次元裂缝奖励物品不存在");
        return id;
    }

    static int Amount(int count)
    {
        if (count < 1 || count > 1_000_000) throw new InvalidDataException("次元裂缝奖励数量无效");
        return count;
    }
}

internal sealed record CrackDimensionRewardServices(CrackDimensionCatalog Table, ItemCatalog Items, GoldLimitCatalog GoldLimits, EpicPieceCatalog Pieces);
