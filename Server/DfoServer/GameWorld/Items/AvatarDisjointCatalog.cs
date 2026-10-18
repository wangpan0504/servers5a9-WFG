using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal sealed record AvatarDisjointRewardDefinition(int ItemId, int Weight, int Count, bool Special);
internal sealed record AvatarDisjointRewardPool(int PickCount, ImmutableArray<AvatarDisjointRewardDefinition> Rewards);
internal sealed record AvatarDisjointGradeTable(int Grade, int MinimumValue, int MaximumValue,
    ImmutableArray<AvatarDisjointRewardPool> Pools);

internal sealed class AvatarDisjointCatalog
{
    const int MaximumGradeTables = 32;
    const string PathPattern = "etc/avatardisjoint/emblemlistinfo_{0}.etc";
    readonly ImmutableArray<AvatarDisjointGradeTable> tables;

    public AvatarDisjointCatalog(PvfArchive archive)
        : this(path => archive.Contains(path) ? archive.ReadText(path) : null) { }

    internal AvatarDisjointCatalog(Func<string, string?> read)
    {
        var loaded = ImmutableArray.CreateBuilder<AvatarDisjointGradeTable>();
        for (int grade = 0; grade < MaximumGradeTables; grade++)
        {
            string path = string.Format(System.Globalization.CultureInfo.InvariantCulture, PathPattern, grade);
            string? text = read(path);
            if (text is null)
                break;
            loaded.Add(Parse(text, grade));
        }
        if (loaded.Count == 0)
            throw new InvalidDataException("PVF缺少时装分解等级奖励表");
        tables = loaded.ToImmutable();
    }

    public AvatarDisjointGradeTable? Get(int grade) =>
        grade >= 0 && grade < tables.Length && tables[grade].Grade == grade ? tables[grade] : null;

    internal static AvatarDisjointGradeTable Parse(string text, int grade)
    {
        if (grade is < 0 or >= MaximumGradeTables)
            throw new InvalidDataException("时装分解奖励表等级无效");

        int minimum = 0, maximum = 0;
        string rangeText = Section(text, "min max value");
        if (rangeText.Length != 0)
        {
            int[] range = Integers(rangeText);
            if (range.Length != 2 || range[0] < 0 || range[1] < range[0])
                throw new InvalidDataException($"PVF时装分解等级{grade}的范围无效");
            minimum = range[0];
            maximum = range[1];
        }

        var pools = ImmutableArray.CreateBuilder<AvatarDisjointRewardPool>();
        foreach (Match match in Regex.Matches(text,
            @"^[ \t]*\[result info\][ \t]*(?:\r?\n)?(.*?)(?=^[ \t]*\[|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            int[] values = Integers(match.Groups[1].Value);
            if (values.Length < 5 || (values.Length - 1) % 4 != 0 || values[0] <= 0)
                throw new InvalidDataException($"PVF时装分解等级{grade}的奖励池行宽无效");
            var rewards = ImmutableArray.CreateBuilder<AvatarDisjointRewardDefinition>();
            long totalWeight = 0;
            for (int at = 1; at < values.Length; at += 4)
            {
                int itemId = values[at], weight = values[at + 1], count = values[at + 2], special = values[at + 3];
                if (itemId <= 0 || weight <= 0 || count <= 0 || special is not (0 or 1))
                    throw new InvalidDataException($"PVF时装分解等级{grade}的奖励项无效");
                totalWeight = checked(totalWeight + weight);
                if (totalWeight > int.MaxValue)
                    throw new InvalidDataException($"PVF时装分解等级{grade}的奖励权重溢出");
                // special 是资源中的展示分类；本版两个 ACK 都只携带物品编号与增量，仍完整保留该字段而不改写抽取权重。
                rewards.Add(new(itemId, weight, count, special != 0));
            }
            pools.Add(new(values[0], rewards.ToImmutable()));
        }
        if (pools.Count == 0)
            throw new InvalidDataException($"PVF时装分解等级{grade}没有奖励池");
        return new(grade, minimum, maximum, pools.ToImmutable());
    }
}
