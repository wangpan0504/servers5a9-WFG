using System.Collections.Frozen;
using System.Globalization;

namespace DfoServer.GameWorld.Items;

internal sealed record ExpertJobDisjointResult(int ItemId, float Multiplier, int AdditionalTable, int BigWinTable, int BigWinPercent);
internal sealed record ExpertJobDisjointSelection(int MinimumGrade, int MaximumGrade, int ItemId, int Weight, float Divisor);

internal sealed class ExpertJobDisjointRules
{
    public static readonly ExpertJobDisjointRules Empty = new("", "", "", 0);
    readonly FrozenDictionary<(int Machine, int Rarity, int State), ExpertJobDisjointResult> results;
    readonly FrozenDictionary<int, ExpertJobDisjointSelection[]> additional;
    readonly FrozenDictionary<int, ExpertJobDisjointSelection[]> bigWins;
    readonly FrozenDictionary<int, int> ownMachineBigWin;
    public int BaseConst { get; }

    public ExpertJobDisjointRules(string resultText, string additionalText, string bigWinText, int baseConst, string ownMachineBigWinText = "")
    {
        var values = Numbers(resultText);
        if (values.Length % 8 != 0 || values.Length != 0 && baseConst <= 0)
            throw new InvalidDataException("分解机基础表或分母无效");
        BaseConst = baseConst;
        additional = Selections(additionalText);
        bigWins = Selections(bigWinText);
        var ownRates = Numbers(ownMachineBigWinText);
        if (ownRates.Length % 2 != 0) throw new InvalidDataException("自用分解机大成功加成必须为档位和概率对");
        var own = new Dictionary<int, int>();
        for (int at = 0; at < ownRates.Length; at += 2)
            if (!own.TryAdd(Integer(ownRates[at], 1, 255), Integer(ownRates[at + 1], 0, 100)))
                throw new InvalidDataException("自用分解机大成功档位重复");
        ownMachineBigWin = own.ToFrozenDictionary();
        var rows = new Dictionary<(int, int, int), ExpertJobDisjointResult>();
        for (int at = 0; at < values.Length; at += 8)
        {
            int machine = Integer(values[at], 0, 254), rarity = Integer(values[at + 1], 0, 6), state = Integer(values[at + 2], 0, 2);
            int item = Integer(values[at + 3], 1, int.MaxValue);
            float multiplier = Positive(values[at + 4]);
            int ordinary = Integer(values[at + 5], 0, int.MaxValue), big = Integer(values[at + 6], 0, int.MaxValue);
            int chance = Integer(values[at + 7], 0, 100);
            if (ordinary != 0 && !additional.ContainsKey(ordinary) || big != 0 && !bigWins.ContainsKey(big) ||
                !rows.TryAdd((machine, rarity, state), new(item, multiplier, ordinary, big, chance)))
                throw new InvalidDataException("分解机奖励池引用缺失或基础键重复");
        }
        results = rows.ToFrozenDictionary();
    }

    public bool TryGet(int machine, int rarity, int state, out ExpertJobDisjointResult result) =>
        results.TryGetValue((machine, rarity, state), out result!);

    public IReadOnlyList<ExpertJobDisjointSelection> Pool(int table, bool bigWin) =>
        (bigWin ? bigWins : additional).GetValueOrDefault(table) ?? [];

    public int OwnMachineBigWinPercent(int machineGrade) => ownMachineBigWin.GetValueOrDefault(machineGrade);

    static FrozenDictionary<int, ExpertJobDisjointSelection[]> Selections(string text)
    {
        var values = Numbers(text);
        if (values.Length % 6 != 0) throw new InvalidDataException("分解机附加产物必须为六列");
        var rows = new Dictionary<int, List<ExpertJobDisjointSelection>>();
        for (int at = 0; at < values.Length; at += 6)
        {
            int table = Integer(values[at], 1, int.MaxValue);
            int minimum = Integer(values[at + 1], 0, 255), maximum = Integer(values[at + 2], minimum, 255);
            int item = Integer(values[at + 3], 1, int.MaxValue), weight = Integer(values[at + 4], 0, 10000);
            float divisor = Positive(values[at + 5]);
            if (!rows.TryGetValue(table, out var list)) rows.Add(table, list = []);
            list.Add(new(minimum, maximum, item, weight, divisor));
        }
        return rows.ToFrozenDictionary(row => row.Key, row => row.Value.ToArray());
    }

    static double[] Numbers(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();

    static int Integer(double value, int minimum, int maximum) => double.IsFinite(value) && value == Math.Truncate(value) &&
        value >= minimum && value <= maximum ? (int)value : throw new InvalidDataException("分解机整数字段越界");

    static float Positive(double value) => float.IsFinite((float)value) && (float)value > 0
        ? (float)value : throw new InvalidDataException("分解机倍率或数量除数无效");
}
