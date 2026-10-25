using System.Collections.Immutable;

namespace PvfLib;

public sealed record OperationHopeMapFile(int MapId, ImmutableArray<int> NextAreas);
public sealed record OperationHopeAreaFile(int Id, ImmutableArray<OperationHopeMapFile> Maps, int QuestId, int MissionValue);

// 只解释原始块结构；概率单位、任务分配和副本阶段由对应领域决定。
public sealed record OperationHopeFile(ImmutableArray<int> MissionIds, ImmutableArray<OperationHopeAreaFile> Areas,
    int FatigueValue, int NeedFatigue, ImmutableArray<PvfTokenSection> Fields)
{
    public static OperationHopeFile Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("代号希望声明未正确闭合");
        var fields = document.Sections;
        var missions = Integers(Single(fields, "mission info list", true));
        var areas = ImmutableArray.CreateBuilder<OperationHopeAreaFile>();
        foreach (var area in fields.Where(row => row.Name == "area"))
        {
            if (!area.IsBlock || area.Values is not [{ Type: 0 } id] ||
                area.Children.Any(row => row.Name is not ("map" or "mission")))
                throw new InvalidDataException("代号希望区域结构无效");
            var mission = Integers(Single(area.Children, "mission", true));
            if (mission.Length != 2) throw new InvalidDataException("代号希望区域任务必须为两个整数");
            var maps = ImmutableArray.CreateBuilder<OperationHopeMapFile>();
            foreach (var map in area.Children.Where(row => row.Name == "map"))
            {
                if (!map.IsBlock || map.Values is not [{ Type: 0 } mapId] ||
                    map.Children.Any(row => row.Name != "next area"))
                    throw new InvalidDataException("代号希望地图结构无效");
                var next = map.Children.IsEmpty ? ImmutableArray<int>.Empty : Integers(Single(map.Children, "next area", true));
                maps.Add(new(mapId.Value, next));
            }
            areas.Add(new(id.Value, maps.ToImmutable(), mission[0], mission[1]));
        }
        return new(missions, areas.ToImmutable(), Integer("fatigue value"), Integer("need fatigue"), fields);

        int Integer(string name)
        {
            var values = Integers(Single(fields, name, false));
            return values.Length == 1 ? values[0] : throw new InvalidDataException("代号希望单值字段长度无效: " + name);
        }
    }

    private static PvfTokenSection Single(ImmutableArray<PvfTokenSection> fields, string name, bool block)
    {
        var rows = fields.Where(row => row.Name == name).ToArray();
        if (rows is not [var row] || row.IsBlock != block || !row.Children.IsEmpty)
            throw new InvalidDataException("代号希望字段缺失、重复或结构无效: " + name);
        return row;
    }

    private static ImmutableArray<int> Integers(PvfTokenSection field)
    {
        if (field.Values.Any(token => token.Type != 0)) throw new InvalidDataException("代号希望字段不是整数列表: " + field.Name);
        return field.Values.Select(token => token.Value).ToImmutableArray();
    }
}
