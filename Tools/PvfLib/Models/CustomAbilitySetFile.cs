using System.Collections.Immutable;
using System.Globalization;
using static PvfLib.PvfScript;
using static PvfLib.CustomAbilityFile;

namespace PvfLib;

public sealed record CustomAbilitySetGroup(string Key, ImmutableArray<int> Indexes, int MinimumParts);
public sealed record CustomAbilitySetPoint(int Index, int MinimumParts, int MaximumParts, int MaximumOptions,
    ImmutableArray<(string Grade, int Parts, int Points)> MaximumPoints,
    ImmutableArray<(int ItemId, float Rate)> Materials, float DiscountRate);
public sealed record CustomAbilitySetFile(ImmutableArray<CustomAbilitySetPoint> Points,
    ImmutableArray<(int Option, int Points)> Spend, ImmutableArray<CustomAbilitySetGroup> Groups)
{
    public static CustomAbilitySetFile Parse(string text)
    {
        int One(string body, string tag) => Numbers(Section(body, tag)) is [int value]
            ? value : throw new InvalidDataException("自定义套装整数声明无效");
        var points = Blocks(text, "point info").Select(body =>
        {
            string[] maximum = Tokens(RequiredBlock(body, "max point table"));
            string[] materials = Tokens(RequiredBlock(body, "material rate by point"));
            if (maximum.Length % 3 != 0 || materials.Length % 2 != 0)
                throw new InvalidDataException("自定义套装点数或材料表不完整");
            int Int(string value) => int.Parse(value, CultureInfo.InvariantCulture);
            float Float(string value) => float.Parse(value, CultureInfo.InvariantCulture);
            return new CustomAbilitySetPoint(One(body, "part set index"), One(body, "min part count"),
                One(body, "max part count"), One(body, "enchant max count"),
                Enumerable.Range(0, maximum.Length / 3).Select(i =>
                    (maximum[i * 3], Int(maximum[i * 3 + 1]), Int(maximum[i * 3 + 2]))).ToImmutableArray(),
                Enumerable.Range(0, materials.Length / 2).Select(i =>
                    (Int(materials[i * 2]), Float(materials[i * 2 + 1]))).ToImmutableArray(),
                Float(Section(body, "discount enchant rate").Trim()));
        }).ToImmutableArray();
        int[] spend = Numbers(RequiredBlock(text, "spend grade point"));
        if (spend.Length % 2 != 0) throw new InvalidDataException("自定义套装消耗点数表不完整");
        var groups = Blocks(text, "group").Select(body =>
        {
            string[] condition = Tokens(Block(body, "condition"));
            if (condition.Length != 0 && (condition.Length != 2 || condition[0] != "part count"))
                throw new InvalidDataException("自定义套装条件声明无效");
            return new CustomAbilitySetGroup(Tokens(Section(body, "key")) is [var key] ? key :
                    throw new InvalidDataException("自定义套装分组无效"),
                Numbers(RequiredBlock(body, "indexes")).ToImmutableArray(),
                condition.Length == 0 ? 0 : int.Parse(condition[1], CultureInfo.InvariantCulture));
        }).ToImmutableArray();
        return new(points, Enumerable.Range(0, spend.Length / 2)
            .Select(i => (spend[i * 2], spend[i * 2 + 1])).ToImmutableArray(), groups);
    }
}
