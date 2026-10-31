using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record AntonNormalSequenceCatalog(ImmutableArray<ImmutableArray<int>> Routes)
{
    public uint? Next(int dungeon)
    {
        // 共用前三图未携带分支身份时选资源第一条路线；变体图始终沿自身对应路线。
        foreach (var route in Routes)
        {
            int index = route.IndexOf(dungeon);
            if (index >= 0) return index + 1 < route.Length ? checked((uint)route[index + 1]) : null;
        }
        throw new InvalidDataException("普通安徒恩不在序列28中");
    }

    internal static AntonNormalSequenceCatalog Load(PvfArchive archive)
    {
        var blocks = Regex.Matches(archive.ReadText("etc/sequential_dungeon_info.etc"),
            @"\[sequential dungeon\]\s*28\s+(.*?)\[/sequential dungeon\]", RegexOptions.Singleline);
        if (blocks.Count != 1 || !blocks[0].Value.Contains("[next dungeon auto enter]", StringComparison.Ordinal))
            throw new InvalidDataException("普通安徒恩序列28缺失或重复");
        var tables = Regex.Matches(blocks[0].Value,
            @"\[branched dungeon index check\]\s*([\d\s]+)\[/branched dungeon index check\]");
        if (tables.Count != 1) throw new InvalidDataException("普通安徒恩分支表缺失或重复");
        var values = tables[0].Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != 29 || values[0] != 6) throw new InvalidDataException("普通安徒恩不是四条六图路线");
        var routes = ImmutableArray.CreateBuilder<ImmutableArray<int>>(4);
        for (int branch = 1; branch <= 4; branch++)
        {
            int at = 1 + (branch - 1) * 7;
            var route = values.AsSpan(at + 1, 6).ToArray().ToImmutableArray();
            if (values[at] != branch || !route.AsSpan(0, 3).SequenceEqual([9595, 9596, 9597]) ||
                route[3] != 9598 + branch || route[4] != 9602 + branch || route[5] != 9607)
                throw new InvalidDataException("普通安徒恩路线与变体不匹配");
            routes.Add(route);
        }
        return new(routes.MoveToImmutable());
    }
}
