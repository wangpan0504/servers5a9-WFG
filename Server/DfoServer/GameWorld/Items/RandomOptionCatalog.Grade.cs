using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal readonly record struct RandomOptionGradeBand(int Minimum, int Maximum, long Weight);

internal sealed partial class RandomOptionCatalog
{
    readonly Dictionary<int, ImmutableArray<RandomOptionGradeBand>> gradeBands = new();
    readonly HashSet<int> fixedGrades = new();

    void ReadGradeRules(string text)
    {
        string block = PvfScript.Block(text, "postfix grade modification");
        if (string.IsNullOrWhiteSpace(block)) return;
        var rows = Integers(block, 5);
        if (rows.Any(row => row[0] is not (2 or 3) || row[1] is < 1 or > 4 || row[2] < 0 ||
            row[3] <= row[2] || row[3] > 256 || row[4] < 0) ||
            rows.GroupBy(row => (row[0], row[1])).Any(group => group.Count() != 1))
            throw new InvalidDataException("魔封品级调整权重或区间无效");
        foreach (var group in rows.GroupBy(row => row[0]))
        {
            if (group.Sum(row => (long)row[4]) <= 0) throw new InvalidDataException("魔封品级调整没有有效权重");
            gradeBands.Add(group.Key, group.Select(row => new RandomOptionGradeBand(row[2], row[3], row[4])).ToImmutableArray());
        }
        fixedGrades.UnionWith(PvfScript.Integers(PvfScript.Block(text, "unable to modify postfix grade")));
        fixedGrades.UnionWith(PvfScript.Integers(PvfScript.Block(text, "gradeless postfix")));
    }

    public ImmutableArray<RandomOptionGradeBand> GradeBands(ItemTemplate template) =>
        gradeBands.GetValueOrDefault(template.Rarity, []);
    public bool CanAdjustGrade(byte type) => type != 0 && options.ContainsKey(type) && !fixedGrades.Contains(type);
}
