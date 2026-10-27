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
        if (maze.IsLostLand && (maze.ResourceError is not null || maze.Starts.Count != 1 || maze.Bosses.Count != 1 ||
            maze.Starts[0] == maze.Bosses[0] || maze.QuestConnectionTokens.Count != 0 || maze.MapAlternatives.Count != 0 ||
            maze.LayeredMaps.Count != 0 || maze.ConditionalMaps.Count != 0 || maze.DummyStages.Count != 0 ||
            maze.StoryStages.Count != 0 || maze.BossEntrance is not null || maze.HellParty is not null ||
            maze.IsTrainingRoom || maze.IsClassExperience || maze.IsGriefTower || maze.IsDespairTower || maze.IsUltimateAltar || maze.IsTournament))
            return Unsupported("遗忘之地专用地图结构不匹配");
        // 该标记只由严格资源池设置；仍核对唯一实际地图，不能借它放行普通BB。
        if (maze.IsGriefTower && maze.ResourceError is null && maze.Width == 1 && maze.Height == 1 &&
            grid.Cells is [{ Index: 1 }] && maze.MapSpecification is [{ Kind: "boss" } grief] && grief == maze.GriefRoom &&
            grief.Position == new DungeonPoint(0,0) && maze.Starts.SequenceEqual([grief.Position]) && maze.Bosses.SequenceEqual([grief.Position]) &&
            maze.QuestConnectionTokens.Count == 0 && maze.LayeredMaps.Count == 0 && maze.WarpMaps.Count == 0 &&
            maze.ConditionalMaps.Count == 0 && maze.StoryStages.Count == 0 && maze.BossEntrance is null && maze.HellParty is null)
            return new([new(grief,0)],[],null);
        // 专用单房间保留修炼场BB或体验AA；实际无邻接，不补出口，也不放宽普通地图。
        if ((maze.IsTrainingRoom && grid.Cells is [{ Index: 1 }] || maze.IsClassExperience && grid.Cells is [{ Index: 0 }] ||
            (maze.IsTournament || maze.IsUltimateAltar || maze.IsDespairTower) && grid.Cells is [{ Index: 1 }]) &&
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
        if (maze.IsLostLand && maps.Values.Any(map => (map.Kind == "boss") != (map.Position == maze.Bosses[0]) ||
            map.StoryStageIndex >= 0 || map.LayerIndex >= 0 || map.DummyStageIndex >= 0 || map.IsConditional))
            return Unsupported("遗忘之地地图身份或阶段不匹配");

        var rooms = new List<DungeonStaticRoom>();
        var links = new List<DungeonStaticLink>();
        // 行主序及4/1/2/8分别对应左/右/上/下，绑定ARAD完整查询和方向消费者证据。
        (int X, int Y, byte Bit, byte Opposite)[] directions = [(-1, 0, 4, 1), (1, 0, 1, 4), (0, -1, 2, 8), (0, 1, 8, 2)];
        for (int index = 0; index < grid.Cells!.Count; index++)
        {
            var point = new DungeonPoint(index % grid.Width, index / grid.Width);
            byte mask = grid.Cells[index].Index;
            bool hasMap = maps.TryGetValue(point, out var map);
            if (mask != 0 && !hasMap || !maze.IsLostLand && mask == 0 && hasMap)
                return Unsupported("Greed真实房间与显式地图集合不一致");
            if (mask == 0)
            {
                if (maze.IsLostLand && hasMap)
                {
                    if (!maze.WarpMaps.Any(warp => warp.Destination == point && warp.Source != point && maps.ContainsKey(warp.Source)))
                        return Unsupported("遗忘之地零出口房缺少真实传送来源");
                    rooms.Add(new(map!, 0));
                }
                continue;
            }
            rooms.Add(new(map!, mask));
            foreach (var direction in directions)
            {
                if ((mask & direction.Bit) == 0)
                    continue;
                var target = new DungeonPoint(point.X + direction.X, point.Y + direction.Y);
                if (target.X < 0 || target.X >= grid.Width || target.Y < 0 || target.Y >= grid.Height)
                {
                    if (maze.IsLostLand) continue;
                    return Unsupported("Greed出口指向网格外");
                }
                int targetIndex = target.Y * grid.Width + target.X;
                if (!maps.ContainsKey(target) || (grid.Cells[targetIndex].Index & direction.Opposite) == 0)
                {
                    if (maze.IsLostLand) continue;
                    return Unsupported("Greed出口缺少真实目标地图或对侧出口");
                }
                if (index < targetIndex)
                    links.Add(new(point, target));
            }
        }

        if (rooms.Count == 0 || maze.Starts.Count == 0 || maze.Bosses.Count == 0 ||
            maze.Starts.Concat(maze.Bosses).Any(point => !maps.ContainsKey(point)))
            return Unsupported("起点或Boss点没有对应真实房间");
        // 特殊传送另有运行资格；保留独立房间，不从单侧门、胶囊或可达性补造普通连接。
        if (maze.IsLostLand)
            return new(Array.AsReadOnly(rooms.ToArray()), Array.AsReadOnly(links.ToArray()), null);
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
