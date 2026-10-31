using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record AntonNormalSequenceCatalog(ImmutableArray<ImmutableArray<int>> Routes,
    ImmutableDictionary<int, uint> DisabledAfterClear)
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
        var frozenRoutes = routes.MoveToImmutable();
        var restrictions = Regex.Matches(blocks[0].Value,
            @"\[disable by cleared sequence\]\s*([\d\s]+)\[/disable by cleared sequence\]");
        if (restrictions.Count != 1) throw new InvalidDataException("普通安徒恩阶段关闭表缺失或重复");
        var columns = restrictions[0].Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var disabled = ImmutableDictionary.CreateBuilder<int, uint>();
        for (int at = 0; at < columns.Length;)
        {
            if (at + 2 > columns.Length) throw new InvalidDataException("普通安徒恩阶段关闭行不完整");
            int dungeon = columns[at++], count = columns[at++];
            if (count is < 1 or > 6 || at + count > columns.Length || !frozenRoutes.Any(route => route.Contains(dungeon)))
                throw new InvalidDataException("普通安徒恩阶段关闭行越界");
            uint mask = 0;
            for (int index = 0; index < count; index++)
            {
                int stage = columns[at++];
                if (stage is < 0 or > 5 || (mask & (1u << stage)) != 0)
                    throw new InvalidDataException("普通安徒恩阶段关闭条件越界或重复");
                mask |= 1u << stage;
            }
            if (!disabled.TryAdd(dungeon, mask)) throw new InvalidDataException("普通安徒恩阶段关闭副本重复");
        }
        if (disabled.Count != frozenRoutes.SelectMany(route => route).Distinct().Count())
            throw new InvalidDataException("普通安徒恩阶段关闭表未覆盖全部路线");
        return new(frozenRoutes, disabled.ToImmutable());
    }
}
