using System.Collections.Immutable;

namespace PvfLib;

public readonly record struct RandomOptionTypeRow(int Rarity, int ItemType, int Level, ImmutableArray<int> Weights);
public readonly record struct RandomOptionGroupRow(int Rarity, int ItemType, string Part, int OptionType,
    ImmutableArray<int> Groups, ImmutableArray<int> Weights);
public readonly record struct RandomOptionModificationCostRow(int Level, int CommonGold, int UniqueGold);
public readonly record struct RandomOptionRangeRow(int Rarity, int OptionType, int Minimum, int Maximum);
public readonly record struct RandomOptionValueRatio(float Lower, float Upper, int Weight);

public static class RandomOptionFiles
{
    public static ImmutableArray<RandomOptionTypeRow> Types(PvfTokenSection section)
    {
        var values = Integers(section);
        Require(values.Length % 6 == 0);
        return Enumerable.Range(0, values.Length / 6).Select(row => row * 6)
            .Select(at => new RandomOptionTypeRow(values[at], values[at + 1], values[at + 2],
                [values[at + 3], values[at + 4], values[at + 5]])).ToImmutableArray();
    }

    public static ImmutableArray<RandomOptionGroupRow> Groups(PvfTokenSection section, Func<int, string> resolve)
    {
        Require(section.Children.IsEmpty);
        var values = section.Values;
        var rows = ImmutableArray.CreateBuilder<RandomOptionGroupRow>();
        int at = 0;
        while (at < values.Length)
        {
            int rarity = Integer(), itemType = Integer();
            Require(at < values.Length && values[at].Type == 6);
            string part = resolve(values[at++].Value);
            int optionType = Integer(), count = Integer();
            Require(count is > 0 and <= 255 && values.Length - at >= count * 2);
            var groups = ImmutableArray.CreateBuilder<int>(count);
            var weights = ImmutableArray.CreateBuilder<int>(count);
            for (int i = 0; i < count; i++) groups.Add(Integer());
            for (int i = 0; i < count; i++) weights.Add(Integer());
            rows.Add(new(rarity, itemType, part, optionType, groups.MoveToImmutable(), weights.MoveToImmutable()));
        }
        return rows.ToImmutable();

        int Integer()
        {
            Require(at < values.Length && values[at].Type == 0);
            return values[at++].Value;
        }
    }

    public static ImmutableArray<RandomOptionModificationCostRow> ModificationCosts(PvfTokenSection section)
    {
        var values = Integers(section);
        Require(values.Length % 3 == 0);
        return Enumerable.Range(0, values.Length / 3).Select(row => row * 3)
            .Select(at => new RandomOptionModificationCostRow(values[at], values[at + 1], values[at + 2])).ToImmutableArray();
    }

    public static ImmutableArray<RandomOptionRangeRow> Ranges(PvfTokenSection section)
    {
        var values = Integers(section);
        Require(values.Length % 4 == 0);
        return Enumerable.Range(0, values.Length / 4).Select(row => row * 4)
            .Select(at => new RandomOptionRangeRow(values[at], values[at + 1], values[at + 2], values[at + 3])).ToImmutableArray();
    }

    public static ImmutableArray<RandomOptionValueRatio> ValueRatios(PvfTokenSection section)
    {
        Require(section.Children.IsEmpty && section.Values.Length % 3 == 0);
        var values = section.Values;
        var rows = ImmutableArray.CreateBuilder<RandomOptionValueRatio>();
        for (int at = 0; at < values.Length; at += 3)
        {
            Require(values[at].Type is 0 or 2 && values[at + 1].Type is 0 or 2 && values[at + 2].Type == 0);
            rows.Add(new(Number(values[at]), Number(values[at + 1]), values[at + 2].Value));
        }
        return rows.ToImmutable();

        static float Number(PvfToken token) => token.Type == 2 ? BitConverter.Int32BitsToSingle(token.Value) : token.Value;
    }

    static int[] Integers(PvfTokenSection section)
    {
        Require(section.Children.IsEmpty && section.Values.All(token => token.Type == 0));
        return section.Values.Select(token => token.Value).ToArray();
    }

    static void Require(bool valid)
    {
        if (!valid) throw new InvalidDataException("随机属性脚本字段数量或token类型无效");
    }
}
