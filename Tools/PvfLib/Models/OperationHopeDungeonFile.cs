using System.Collections.Immutable;

namespace PvfLib;

public sealed record OperationHopeWarpFile(int SourceX, int SourceY, int DestinationX, int DestinationY);
public sealed record OperationHopeMazeFile(int Ordinal, bool HiddenBoss, ImmutableArray<int> ClearMaps, int DeclaredCount);

// DGN 的 maze info 是平铺分段；末尾 warp map condition 属于副本，不能只挂到最后一段。
public sealed record OperationHopeDungeonFile(ImmutableArray<OperationHopeMazeFile> Mazes,
    ImmutableArray<OperationHopeWarpFile> Warps, int ContinuousDungeon)
{
    public static OperationHopeDungeonFile Parse(IReadOnlyList<PvfToken> tokens, Func<int,string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("代号希望副本声明未正确闭合");
        var fields = document.Sections;
        var enabled = fields.Where(row => row.Name == "operation hope dungeon").ToArray();
        if (enabled is not [{ Values: [{ Type: 0, Value: 1 }], Children.Length: 0 }])
            throw new InvalidDataException("副本不是显式代号希望声明");
        var continuous = fields.Where(row => row.Name == "continuous dungeon").ToArray();
        if (continuous is not [{ Values: [{ Type: 0 } continuousValue], Children.Length: 0 }])
            throw new InvalidDataException("代号希望连续副本声明无效");
        var mazes = ImmutableArray.CreateBuilder<OperationHopeMazeFile>();
        int ordinal = -1;
        bool hidden = false, hasClear = false;
        foreach (var row in fields)
        {
            if (row.Name == "maze info")
            {
                if (ordinal >= 0 && !hasClear) throw new InvalidDataException("代号希望迷宫缺少通关声明");
                if (!row.Values.IsEmpty || !row.Children.IsEmpty) throw new InvalidDataException("代号希望迷宫分段无效");
                ordinal++;
                hidden = false;
                hasClear = false;
            }
            else if (row.Name == "hidden boss maze")
            {
                if (ordinal < 0 || hasClear || hidden || !row.Values.IsEmpty || !row.Children.IsEmpty)
                    throw new InvalidDataException("代号希望隐藏迷宫标记无效");
                hidden = true;
            }
            else if (row.Name == "clear condition")
            {
                if (ordinal < 0 || hasClear || !row.IsBlock || !row.Values.IsEmpty ||
                    row.Children is not [{ Name: "clear map", Children.Length: 0 } condition])
                    throw new InvalidDataException("代号希望迷宫通关声明缺失或重复");
                var values = condition.Values;
                if (values.Length < 4 || values[0].Type != 6 || resolve(values[0].Value) != "list" ||
                    values[1] is not { Type: 0, Value: > 0 } count || (long)count.Value + 3 != values.Length ||
                    values.Skip(2).Any(value => value.Type != 0))
                    throw new InvalidDataException("代号希望清图列表长度或类型无效");
                mazes.Add(new(ordinal, hidden, values.Skip(2).Take(count.Value).Select(value => value.Value).ToImmutableArray(), values[^1].Value));
                hasClear = true;
            }
        }
        if (ordinal < 0 || !hasClear) throw new InvalidDataException("代号希望迷宫缺少通关声明");
        var warpFields = fields.Where(row => row.Name == "warp map condition").ToArray();
        if (warpFields is not [{ IsBlock: true, Values.Length: 0 } warp] || warp.Children.Length % 2 != 0)
            throw new InvalidDataException("代号希望全局传送声明无效");
        var warps = ImmutableArray.CreateBuilder<OperationHopeWarpFile>();
        for (int at = 0; at < warp.Children.Length; at += 2)
        {
            if (warp.Children[at] is not { Name: "source grid pos", Children.Length: 0, Values: [{ Type: 0 } sx, { Type: 0 } sy] } ||
                warp.Children[at+1] is not { Name: "dest grid pos", Children.Length: 0, Values: [{ Type: 0 } dx, { Type: 0 } dy] })
                throw new InvalidDataException("代号希望全局传送坐标对无效");
            warps.Add(new(sx.Value,sy.Value,dx.Value,dy.Value));
        }
        return new(mazes.ToImmutable(),warps.ToImmutable(),continuousValue.Value);
    }
}
