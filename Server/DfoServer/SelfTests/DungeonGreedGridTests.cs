using static DfoServer.Testing.TestCheck;
using System.Collections;
using System.Text.Json;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class DungeonGreedGridTests
{
    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var dungeon = DungeonCatalog.Load(pvf).Get(3);
        var maze = dungeon.Mazes.Single(value => value.SourceOrdinal == 1);
        var grid = maze.GreedGrid;
        That(maze.QuestConnectionTokens.Select(token => token.Value).SequenceEqual(new[] { 0, 3145, -1 }) &&
            grid.Width == 5 && grid.Height == 2 && grid.SourceText == "AAAAJJFFEE\r\nBBFFGGAAAA", "Dungeon3/3145 maze1尺寸及真实原文");
        That(grid.Supported && grid.UnsupportedReason is null && grid.Format == "ascii-pairs" &&
            grid.Cells!.Select(cell => cell.Index).SequenceEqual(new byte[] { 0, 0, 9, 5, 4, 1, 5, 6, 0, 0 }),
            "真实十格顺序与同冻结runtime vector逐项一致，不赋出口方向");
        That(grid.Cells!.Select(cell => cell.TextOffset).SequenceEqual(new[] { 0, 2, 4, 6, 8, 12, 14, 16, 18, 20 }) &&
            grid.Cells!.Select(cell => cell.SourceOrdinal).SequenceEqual(Enumerable.Range(0, 10)) &&
            grid.Cells!.Select(cell => cell.Code).SequenceEqual(new[] { "AA", "AA", "JJ", "FF", "EE", "BB", "FF", "GG", "AA", "AA" }),
            "索引、UTF16原文偏移及文字编码保持");
        That(grid.Cells is IList list && list.IsReadOnly, "成功格序列不可变");
        var ordinary = dungeon.Mazes[0].GreedGrid;
        That(ordinary.Supported && ordinary.SourceText == "AAIIJJEE\r\n BBHHGGAA" &&
            ordinary.Cells!.Select(cell => cell.Index).SequenceEqual(new byte[] { 0, 8, 9, 4, 1, 7, 6, 0 }) &&
            ordinary.Cells![4].TextOffset == 11, "普通maze保留换行后的真实前导空格");
        string allCodes = string.Join(' ', Enumerable.Range(0, 16).Select(index => new string((char)('A' + index), 2)));
        var complete = DungeonGreedGrid.Read(16, 1, allCodes);
        That(complete.Supported && complete.Cells!.Select(cell => (int)cell.Index).SequenceEqual(Enumerable.Range(0, 16)),
            "完整AA至PP表对应0至15，PP不溢出或夹取");
        var whitespace = DungeonGreedGrid.Read(2, 2, "\r\n AA BB\rCC\nDD ");
        That(whitespace.Supported && whitespace.Cells!.Select(cell => cell.TextOffset).SequenceEqual(new[] { 3, 6, 9, 12 }),
            "受支持策略仅在编码边界消费CR/LF/ASCII space");
        That(DungeonGreedGrid.Read(1, 2, "AABB").Supported && DungeonGreedGrid.Read(2, 1, "AA\r\nBB").Supported,
            "换行只作已识别分隔符，不推断网格坐标或要求每行width项");
        foreach (string unknown in new[] { "AB", "aa", "QQ", "A", "A A", "A\rA", "A\nA", "\u253C", "\u253C\0",
            "\tAA", "AA\t", "\vAA", "\fAA", "AA\u00A0", "AA\u2003", "", " \r\n " })
        {
            var unsupported = DungeonGreedGrid.Read(1, 1, unknown);
            That(!unsupported.Supported && unsupported.Cells is null && unsupported.Format == "unsupported" &&
                unsupported.UnsupportedReason is not null && unsupported.SourceText == unknown,
                "未知字母、残缺对、非已证空白及Unicode原文保持而不补零");
        }
        foreach (var input in new (int Width, int Height, string Text)[] { (0, 1, "AA"), (-1, 1, "AA"), (1, 0, "AA"),
            (int.MaxValue, int.MaxValue, "AA"), (2, 1, "AA"), (1, 1, "AABB"), (2, 1, "AAB") })
        {
            var unsupported = DungeonGreedGrid.Read(input.Width, input.Height, input.Text);
            That(!unsupported.Supported && unsupported.Cells is null && unsupported.UnsupportedReason is not null,
                "维度非法、乘法超界、格数不足/过量及后段错误不发布部分格");
        }
        var changed = maze with { Greed = "QQ" };
        That(!changed.GreedGrid.Supported && maze.GreedGrid.Supported, "record with更新原文不会沿用旧Greed缓存");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(changed));
        That(!json.RootElement.GetProperty("GreedGrid").GetProperty("Supported").GetBoolean() &&
            json.RootElement.GetProperty("GreedGrid").GetProperty("UnsupportedReason").ValueKind == JsonValueKind.String &&
            json.RootElement.GetProperty("GreedGrid").GetProperty("SourceText").GetString() == "QQ",
            "实际资源查询JSON显式呈现Unsupported及原文");
        var topology = maze.StaticTopology;
        That(topology.Supported && topology.Rooms!.Count == 6 && topology.Links!.Count == 5,
            "3145真实地图六房五连接，两端出口一致");
        DungeonPoint[] route = [new(0, 1), new(1, 1), new(2, 1), new(2, 0), new(3, 0), new(4, 0)];
        That(route.Zip(route.Skip(1)).All(pair => topology.Links!.Any(link =>
            link.First == pair.First && link.Second == pair.Second || link.Second == pair.First && link.First == pair.Second)),
            "3145实际弯折路线逐段对应，不将相邻空格连接");
        That(topology.Rooms!.Select(room => room.Map.Id).Order().SequenceEqual(Enumerable.Range(76121, 6)),
            "六个真实地图身份完整保留");
        foreach (var invalidMaze in new[]
        {
            maze with { Greed = "AAAABBFFEE\r\nBBFFGGAAAA" }, // 上层缺下出口。
            maze with { Greed = "AAAAJJFFBB\r\nBBFFGGAAAA" }, // 右边界向外。
            maze with { Greed = "BBAAJJFFEE\r\nBBFFGGAAAA" }, // 空格缺地图。
            maze with { MapSpecification = maze.MapSpecification!.Skip(1).ToArray() },
            maze with { MapSpecification = maze.MapSpecification!.Append(maze.MapSpecification![0]).ToArray() },
            maze with { Starts = [new(0, 0)] },
            maze with { Bosses = [new(5, 0)] },
            maze with { MapSpecification = null },
            maze with { Greed = "QQ" }
        })
        {
            var unsupported = invalidMaze.StaticTopology;
            That(!unsupported.Supported && unsupported.Rooms is null && unsupported.Links is null &&
                unsupported.UnsupportedReason is not null, "静态资格失败不发布部分路线");
        }
        That(topology.Rooms is IList roomList && roomList.IsReadOnly && topology.Links is IList linkList && linkList.IsReadOnly,
            "静态地图与连接序列不可变");
        var disconnected = maze with
        {
            Width = 4, Height = 1, Greed = "BBEEBBEE",
            MapSpecification = Enumerable.Range(0, 4).Select(index => new DungeonMap("map", new(index, 0), index, $"map/fixture{index}.map")).ToArray(),
            Starts = [new(0, 0)], Bosses = [new(3, 0)]
        };
        That(!disconnected.StaticTopology.Supported && disconnected.StaticTopology.UnsupportedReason == "存在起点不可达的真实房间",
            "每条出口互相吻合仍拒绝不连通的独立房间组");
        Console.WriteLine($"PASS {Count - checks} Greed真实索引、编码边界与静态双向连接");

    }
}
