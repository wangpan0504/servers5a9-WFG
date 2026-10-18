using System.Globalization;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld.Items;

// `[grow type]` 行宽：职业标签、最低/最高等级、转职分支、抽数，其后物品/权重/数量。
internal static class BoosterGrowTypePools
{
    public static bool TryParse(string data, out BoosterPool? pool, out string? pending)
    {
        pool = null;
        pending = null;
        string[] tokens = Regex.Matches(data, @"`[^`]*`|\S+").Select(match => match.Value).ToArray();
        if (tokens.Length == 0)
            return Fail("转职奖池行宽无效", out pending);
        string job = tokens[0].Trim('`');
        if (job.Length < 3 || job[0] != '[' || job[^1] != ']' ||
            !Enumerable.Range(0, 16).Any(index => string.Equals(ItemCatalog.JobTag((byte)index), job, StringComparison.OrdinalIgnoreCase)))
            return Fail("转职奖池职业无效", out pending);
        if (tokens.Length < 5)
            return Fail("转职奖池行宽无效", out pending);
        if (!int.TryParse(tokens[1], out int minimum) || minimum < 0 ||
            !int.TryParse(tokens[2], out int maximum) || maximum < minimum ||
            !int.TryParse(tokens[3], out int grow) || grow is < 0 or > 15 ||
            !int.TryParse(tokens[4], out int draws) || draws <= 0)
            return Fail("转职奖池等级、分支或抽数无效", out pending);
        if (tokens.Length == 5)
            return true;
        if ((tokens.Length - 5) % 3 != 0)
            return Fail("转职奖池行宽无效", out pending);
        var rows = new List<(int Id, decimal Weight, int Count)>();
        int scale = 0;
        for (int i = 5; i < tokens.Length; i += 3)
        {
            if (!int.TryParse(tokens[i], out int id) || id <= 0 || !int.TryParse(tokens[i + 2], out int count) || count <= 0 ||
                !decimal.TryParse(tokens[i + 1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal weight) ||
                weight < 0)
                return Fail("转职奖池物品、数量或权重无效", out pending);
            scale = Math.Max(scale, (decimal.GetBits(weight)[3] >> 16) & 0xff);
            rows.Add((id, weight, count));
        }
        try
        {
            decimal factor = 1;
            for (int digit = 0; digit < scale; digit++)
                factor = checked(factor * 10);
            var rewards = rows.Select(row => new BoosterReward(row.Id, checked((long)(row.Weight * factor)), row.Count))
                .ToImmutableArray();
            if (rewards.Sum(row => row.Weight) > 0)
                pool = new(draws, rewards)
                {
                    JobTag = job,
                    MinimumLevel = minimum,
                    MaximumLevel = maximum,
                    GrowType = grow
                };
            return true;
        }
        catch (OverflowException)
        {
            return Fail("转职奖池权重溢出", out pending);
        }
    }

    static bool Fail(string reason, out string? pending)
    {
        pending = reason;
        return false;
    }
}
