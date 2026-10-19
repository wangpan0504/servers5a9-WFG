using System.Collections.Immutable;

namespace PvfLib;

public readonly record struct AmplificationOptionRow(string Label, int Weight, int BaseValue);
public readonly record struct AmplificationMaterialRow(string? Option, int ItemId, int Count);
public readonly record struct AmplificationNamedNumber(string Label, float Value);
public readonly record struct AmplificationLevelRow(int Level, int Weight);
public readonly record struct AmplificationUpgradeRow(int FailureWeight, int PenaltyType, int PenaltyValue, int MaterialId, int MaterialCount);
public readonly record struct LuckyUpgradeRow(int Level, int DownWeight, int SameWeight, int UpWeight);

public static class AmplificationFiles
{
    public static ImmutableArray<int> Integers(PvfTokenSection section)
    {
        Require(section.Children.IsEmpty);
        return section.Values.Select(Integer).ToImmutableArray();
    }

    public static ImmutableArray<float> Numbers(PvfTokenSection section)
    {
        Require(section.Children.IsEmpty && section.Values.All(token => token.Type is 0 or 2));
        return section.Values.Select(token => token.Type == 2 ? BitConverter.Int32BitsToSingle(token.Value) : token.Value).ToImmutableArray();
    }

    public static ImmutableArray<AmplificationUpgradeRow> UpgradeRows(PvfTokenSection section)
    {
        Require(section.Children.IsEmpty && section.Values.Length > 1 && section.Values[0].Type == 6 &&
            (section.Values.Length - 1) % 24 == 0);
        var rows = ImmutableArray.CreateBuilder<AmplificationUpgradeRow>();
        for (int at = 1; at < section.Values.Length; at += 24)
        {
            Require(section.Values.Skip(at).Take(24).All(token => token.Type is 0 or 2));
            rows.Add(new(Integer(section.Values[at + 8]), Integer(section.Values[at + 10]), Integer(section.Values[at + 11]),
                Integer(section.Values[at + 14]), Integer(section.Values[at + 15])));
        }
        return rows.ToImmutable();
    }

    public static ImmutableArray<LuckyUpgradeRow> LuckyRows(PvfTokenSection section)
    {
        Require(section.Children.IsEmpty && section.Values.Length > 1 && section.Values[0].Type == 6 &&
            (section.Values.Length - 1) % 4 == 0);
        var rows = ImmutableArray.CreateBuilder<LuckyUpgradeRow>();
        for (int at = 1; at < section.Values.Length; at += 4)
            rows.Add(new(Integer(section.Values[at]), Integer(section.Values[at + 1]), Integer(section.Values[at + 2]), Integer(section.Values[at + 3])));
        return rows.ToImmutable();
    }

    public static ImmutableArray<AmplificationOptionRow> Options(PvfTokenSection section, Func<int, string> resolve)
    {
        Require(section.Children.IsEmpty && section.Values.Length % 3 == 0);
        var rows = ImmutableArray.CreateBuilder<AmplificationOptionRow>();
        for (int at = 0; at < section.Values.Length; at += 3)
        {
            Require(section.Values[at].Type == 6);
            rows.Add(new(resolve(section.Values[at].Value), Integer(section.Values[at + 1]), Integer(section.Values[at + 2])));
        }
        return rows.ToImmutable();
    }

    public static ImmutableArray<AmplificationMaterialRow> Materials(PvfTokenSection section, Func<int, string> resolve, bool withOption)
    {
        int width = withOption ? 3 : 2;
        Require(section.Children.IsEmpty && section.Values.Length % width == 0);
        var rows = ImmutableArray.CreateBuilder<AmplificationMaterialRow>();
        for (int at = 0; at < section.Values.Length; at += width)
        {
            if (withOption) Require(section.Values[at].Type == 6);
            int start = at + (withOption ? 1 : 0);
            rows.Add(new(withOption ? resolve(section.Values[at].Value) : null,
                Integer(section.Values[start]), Integer(section.Values[start + 1])));
        }
        return rows.ToImmutable();
    }

    public static ImmutableArray<AmplificationNamedNumber> NamedNumbers(PvfTokenSection section, Func<int, string> resolve)
    {
        Require(section.Children.IsEmpty && section.Values.Length % 2 == 0);
        var rows = ImmutableArray.CreateBuilder<AmplificationNamedNumber>();
        for (int at = 0; at < section.Values.Length; at += 2)
        {
            Require(section.Values[at].Type == 6 && section.Values[at + 1].Type is 0 or 2);
            var value = section.Values[at + 1];
            rows.Add(new(resolve(section.Values[at].Value), value.Type == 2 ? BitConverter.Int32BitsToSingle(value.Value) : value.Value));
        }
        return rows.ToImmutable();
    }

    public static ImmutableArray<AmplificationLevelRow> Levels(PvfTokenSection section)
    {
        Require(section.Children.IsEmpty && section.Values.Length % 2 == 0);
        var rows = ImmutableArray.CreateBuilder<AmplificationLevelRow>();
        for (int at = 0; at < section.Values.Length; at += 2)
            rows.Add(new(Integer(section.Values[at]), Integer(section.Values[at + 1])));
        return rows.ToImmutable();
    }

    public static int Scalar(PvfTokenSection section)
    {
        Require(section.Children.IsEmpty && section.Values.Length == 1);
        return Integer(section.Values[0]);
    }

    static int Integer(PvfToken value)
    {
        Require(value.Type == 0);
        return value.Value;
    }

    static void Require(bool valid)
    {
        if (!valid) throw new InvalidDataException("异次元属性声明字段数量或token类型无效");
    }
}
