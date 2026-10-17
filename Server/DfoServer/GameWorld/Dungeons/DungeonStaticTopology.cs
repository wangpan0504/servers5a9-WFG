namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonStaticRoom(DungeonMap Map, byte ExitMask);
internal sealed record DungeonStaticLink(DungeonPoint First, DungeonPoint Second);

// 只核对Greed及显式地图的静态一致性；不能充当动态门、怪物或正式准入资格。
internal sealed record DungeonStaticTopology(
    IReadOnlyList<DungeonStaticRoom>? Rooms,
    IReadOnlyList<DungeonStaticLink>? Links,
    string? UnsupportedReason)
{
    public bool Supported => Rooms is not null && Links is not null;

    public static DungeonStaticTopology Read(DungeonMaze maze)
    {
        var grid = maze.GreedGrid;
        if (!grid.Supported)
            return Unsupported(grid.UnsupportedReason!);
        if (maze.MapSpecification is null)
            return Unsupported("未提供显式地图，不能生成静态连接");
        // 专用单房间保留修炼场BB或体验AA；实际无邻接，不补出口，也不放宽普通地图。
        if ((maze.IsTrainingRoom && grid.Cells is [{ Index: 1 }] || maze.IsClassExperience && grid.Cells is [{ Index: 0 }] ||
            maze.IsTournament && grid.Cells is [{ Index: 1 }]) &&
            maze.Width == 1 && maze.Height == 1 && maze.MapSpecification is [var training] &&
            training.Kind == "boss" && training.Id >= 0 && !string.IsNullOrWhiteSpace(training.Path) &&
            training.Position == new DungeonPoint(0,0) && maze.Starts.SequenceEqual([training.Position]) &&
            maze.Bosses.SequenceEqual([training.Position]))
            return new([new(training,0)],[],null);
        var maps = new Dictionary<DungeonPoint, DungeonMap>();
        foreach (var map in maze.MapSpecification)
        {
            if (map.Kind is not ("map" or "boss") || map.Id < 0 || string.IsNullOrWhiteSpace(map.Path))
                return Unsupported("地图身份或种类未支持");
            if (map.Position.X < 0 || map.Position.X >= grid.Width || map.Position.Y < 0 || map.Position.Y >= grid.Height ||
                !maps.TryAdd(map.Position, map) && !maze.StoryStages.Any(stage => stage.Position == map.Position))
                return Unsupported("地图坐标越界或同格有多个地图候选");
            if (map.StoryStageIndex < 0) maps[map.Position] = map;
        }

        var rooms = new List<DungeonStaticRoom>();
        var links = new List<DungeonStaticLink>();
        // 行主序及4/1/2/8分别对应左/右/上/下，绑定ARAD完整查询和方向消费者证据。
        (int X, int Y, byte Bit, byte Opposite)[] directions = [(-1, 0, 4, 1), (1, 0, 1, 4), (0, -1, 2, 8), (0, 1, 8, 2)];
        for (int index = 0; index < grid.Cells!.Count; index++)
        {
            var point = new DungeonPoint(index % grid.Width, index / grid.Width);
            byte mask = grid.Cells[index].Index;
            bool hasMap = maps.TryGetValue(point, out var map);
            if ((mask != 0) != hasMap)
                return Unsupported("Greed真实房间与显式地图集合不一致");
            if (mask == 0)
                continue;
            rooms.Add(new(map!, mask));
            foreach (var direction in directions)
            {
                if ((mask & direction.Bit) == 0)
                    continue;
                var target = new DungeonPoint(point.X + direction.X, point.Y + direction.Y);
                if (target.X < 0 || target.X >= grid.Width || target.Y < 0 || target.Y >= grid.Height)
                    return Unsupported("Greed出口指向网格外");
                int targetIndex = target.Y * grid.Width + target.X;
                if (!maps.ContainsKey(target) || (grid.Cells[targetIndex].Index & direction.Opposite) == 0)
                    return Unsupported("Greed出口缺少真实目标地图或对侧出口");
                if (index < targetIndex)
                    links.Add(new(point, target));
            }
        }

        if (rooms.Count == 0 || maze.Starts.Count == 0 || maze.Bosses.Count == 0 ||
            maze.Starts.Concat(maze.Bosses).Any(point => !maps.ContainsKey(point)))
            return Unsupported("起点或Boss点没有对应真实房间");
        var reached = new HashSet<DungeonPoint> { maze.Starts[0] };
        var pending = new Queue<DungeonPoint>();
        pending.Enqueue(maze.Starts[0]);
        while (pending.TryDequeue(out var point))
        {
            foreach (var link in links)
            {
                DungeonPoint? next = link.First == point ? link.Second : link.Second == point ? link.First : null;
                if (next is { } target && reached.Add(target))
                    pending.Enqueue(target);
            }
        }

        if (reached.Count != rooms.Count)
            return Unsupported("存在起点不可达的真实房间");
        return new(Array.AsReadOnly(rooms.ToArray()), Array.AsReadOnly(links.ToArray()), null);

        static DungeonStaticTopology Unsupported(string reason) => new(null, null, reason);
    }
}
