using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record PreyNormalCatalog(ImmutableArray<ImmutableArray<int>> Routes)
{
    public const uint SequenceId = 203;
    public const int FinalDungeon = 400000046;
    public ImmutableArray<int> Route(int branch) => branch is >= 1 and <= 6 ? Routes[branch - 1] :
        throw new InvalidDataException("普通普雷分支越界");

    internal static PreyNormalCatalog Load(PvfArchive archive)
    {
        string text = archive.ReadText("etc/sequential_dungeon_info.etc");
        var blocks = Regex.Matches(text, @"\[sequential dungeon\]\s*203\s+(.*?)\[/sequential dungeon\]", RegexOptions.Singleline);
        if (blocks.Count != 1) throw new InvalidDataException("普通普雷序列203缺失或重复");
        string block = blocks[0].Groups[1].Value;
        if (!block.Contains("[is branched sequential dungeon]", StringComparison.Ordinal) ||
            !block.Contains("[next dungeon auto enter]", StringComparison.Ordinal))
            throw new InvalidDataException("普通普雷分支或续图声明缺失");
        var table = Regex.Matches(block, @"\[branched dungeon index check\]\s*([\d\s]+)\[/branched dungeon index check\]");
        if (table.Count != 1) throw new InvalidDataException("普通普雷路线表缺失或重复");
        var values = table[0].Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != 37 || values[0] != 5) throw new InvalidDataException("普通普雷不是六条五图路线");
        var routes = ImmutableArray.CreateBuilder<ImmutableArray<int>>(6);
        for (int branch = 1; branch <= 6; branch++)
        {
            int at = 1 + (branch - 1) * 6;
            var route = values.AsSpan(at + 1, 5).ToArray().ToImmutableArray();
            if (values[at] != branch || route.Distinct().Count() != 5 || route[^1] != FinalDungeon ||
                route.Any(id => id is < 400000032 or > FinalDungeon))
                throw new InvalidDataException("普通普雷路线键、地图或终点不匹配");
            routes.Add(route);
        }
        if (routes.SelectMany(route => route).Distinct().Count() != 15)
            throw new InvalidDataException("普通普雷路线未覆盖十五图");
        return new(routes.MoveToImmutable());
    }

    // 本服兼容日历：06:00业务日由调用方提供，六日循环的首日为2019-06-06；不冒充官方历史轮换。
    public static int BranchForDay(int day)
    {
        var date = new DateOnly(day / 10000, day / 100 % 100, day % 100);
        int offset = date.DayNumber - new DateOnly(2019, 6, 6).DayNumber;
        return (offset % 6 + 6) % 6 + 1;
    }
}
