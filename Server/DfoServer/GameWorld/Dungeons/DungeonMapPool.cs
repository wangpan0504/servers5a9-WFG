using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonMapPoolEntry(int Id, string Path, ImmutableArray<int> Dungeons, string Kind, ushort Layouts);

// 原始MAP的归属、type及greed决定候选，不以相似文件名猜测缺失剧情地图。
internal sealed class DungeonMapPool(ImmutableArray<DungeonMapPoolEntry> entries, IReadOnlyDictionary<int,string>? paths = null)
{
    private readonly IReadOnlyDictionary<int,DungeonMapPoolEntry> byId = entries.ToDictionary(row => row.Id);

    public static DungeonMapPool Load(PvfArchive archive, IReadOnlyDictionary<int,string> paths)
    {
        var rows = ImmutableArray.CreateBuilder<DungeonMapPoolEntry>();
        foreach (var pair in paths.OrderBy(row => row.Key))
        {
            try
            {
                var row = Parse(pair.Key,pair.Value,archive.ReadTokens(pair.Value),archive.ResolveString);
                if (row is not null) rows.Add(row);
            }
            catch (Exception error) when (error is InvalidDataException or FileNotFoundException or NotSupportedException) { }
        }
        return new(rows.ToImmutable(),paths);
    }

    internal static DungeonMapPoolEntry? Parse(int id, string path, IReadOnlyList<PvfToken> tokens, Func<int,string> resolve)
    {
        var fields = new Dictionary<string,IReadOnlyList<PvfToken>>(StringComparer.Ordinal);
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3) continue;
            string name = resolve(tokens[at].Value);
            if (name is not ("[dungeon]" or "[type]" or "[greed]")) continue;
            if (!fields.TryAdd(name,tokens.Skip(at+1).TakeWhile(token => token.Type != 3).ToArray())) return null;
        }
        if (!fields.TryGetValue("[dungeon]",out var owned) || owned.Count == 0 || owned.Any(token => token.Type != 0 || token.Value <= 0) ||
            !fields.TryGetValue("[type]",out var kind) || kind is not [{ Type: 6 }] ||
            !fields.TryGetValue("[greed]",out var greed) || greed is not [{ Type: 6 }]) return null;
        string type = resolve(kind[0].Value);
        if (type is not ("[normal]" or "[boss]" or "[dummy]")) return null;
        string raw = resolve(greed[0].Value);
        int count = raw.Count(ch => ch is not (' ' or '\r' or '\n')) / 2;
        var grid = DungeonGreedGrid.Read(count,1,raw);
        if (!grid.Supported || grid.Cells!.Count % 2 != 0) return null;
        ushort layouts = 0;
        for (int at = 0; at < grid.Cells.Count; at += 2)
        {
            // Full03 04274282..042742E2按两组greed值存pair；先启用两个值一致的方向，异值消费者另行确认。
            if (grid.Cells[at].Index == grid.Cells[at+1].Index) layouts |= checked((ushort)(1 << grid.Cells[at].Index));
        }
        return layouts == 0 ? null : new(id,path,owned.Select(token => token.Value).Distinct().ToImmutableArray(),type,layouts);
    }

    public DungeonMaze Resolve(DungeonDefinition definition, DungeonMaze maze, Func<int,int> draw, bool hellParty = false)
    {
        if (maze.QuestConnectionTokens.Count != 0 || maze.ResourceError is not null || !maze.GreedGrid.Supported ||
            maze.Starts.Count == 0 || maze.Bosses.Count == 0) return maze;
        if (maze.DummyStages.Count != 0) return maze;
        // 遗忘之地包含显式AA传送房和非对称出口。先按原生Boss候选规则补齐资源，
        // 不套普通网格重建（会丢掉AA房），也不以此宣告普通双向拓扑可用。
        if (definition.Id == 4000 && definition.DungeonType == "lost land")
            return ResolveLostLand(definition, maze);
        var maps = (maze.MapSpecification ?? []).ToDictionary(row => row.Position);
        // 原端按MAP自身归属注册；被任务maze引用不等于任务专用，不能从普通池排除共享模板。
        var pool = entries.Where(row => row.Dungeons.Contains(definition.Id)).ToArray();
        var starts = maze.Starts.Where(point => !hellParty || point != maze.HellParty?.Room).ToArray();
        var bosses = maze.Bosses.Where(point => !hellParty || point != maze.HellParty?.Room).Distinct().ToArray();
        if (starts.Length == 0 || bosses.Length == 0) return maze;
        var start = starts[0]; var boss = bosses[0];
        // 夫妻副本DGN仅声明BB单房，MAP以[dungeon]3541反向注册实际Boss图。
        if (definition.IsWeddingCouple)
        {
            if (maze.Width != 1 || maze.Height != 1 || maze.Starts.Count != 1 || maze.Bosses.Count != 1 ||
                start != new DungeonPoint(0, 0) || boss != start || maze.GreedGrid.Cells is not [{ Index: 1 }] ||
                maze.MapSpecification is not null || maze.MapAlternatives.Count != 0 || maze.LayeredMaps.Count != 0 ||
                maze.ConditionalMaps.Count != 0 || maze.WarpMaps.Count != 0 || maze.StoryStages.Count != 0 ||
                maze.BossEntrance is not null || maze.HellParty is not null ||
                pool is not [{ Kind: "[boss]", Layouts: 2, Dungeons.Length: 1 } candidate] ||
                candidate.Dungeons[0] != definition.Id ||
                !candidate.Path.Equals("map/chn_wedding_couple_dungeon/chn_wedding_couple_01.map", StringComparison.OrdinalIgnoreCase))
                return maze;
            var room = new DungeonMap("boss", start, candidate.Id, candidate.Path);
            return maze with { WeddingCoupleRoom = room, MapSpecification = [room] };
        }
        // 绝望单层的BB是专用单房；只从同一DGN归属的唯一MAP解析，不通过名字或楼层算地图编号。
        // 第15/25/55层资源包含真实多房间，继续走下方普通连接解析。
        if (definition.IsDespairTower && maze.Width == 1 && maze.Height == 1 &&
            maze.Starts.Count == 1 && maze.Bosses.Count == 1 && start == new DungeonPoint(0, 0) && boss == start &&
            maze.GreedGrid.Cells is [{ Index: 1 }] && maze.MapSpecification is null && maze.MapAlternatives.Count == 0 &&
            maze.LayeredMaps.Count == 0 && maze.ConditionalMaps.Count == 0 && maze.WarpMaps.Count == 0 &&
            maze.StoryStages.Count == 0 && maze.BossEntrance is null && maze.HellParty is null)
        {
            if (pool is [{ Kind: "[normal]" or "[boss]", Layouts: 2 } candidate])
                return maze with { IsDespairTower = true, MapSpecification = [new("boss", start, candidate.Id, candidate.Path)] };
            return maze;
        }
        // 极限祭坛把唯一map声明用于同格Boss房；核对MAP归属与BB，不对普通迷宫放宽出口。
        if (definition.AltarKind == 2 && definition.AltarMaxRounds > 0 && maze.Width == 1 && maze.Height == 1 &&
            maze.Starts.Count == 1 && maze.Bosses.Count == 1 && start == new DungeonPoint(0, 0) && boss == start &&
            maze.GreedGrid.Cells is [{ Index: 1 }] && maze.MapSpecification is [{ Kind: "map" } declared] &&
            declared.Position == start && maze.MapAlternatives.Count == 0 && maze.LayeredMaps.Count == 0 &&
            maze.ConditionalMaps.Count == 0 && maze.WarpMaps.Count == 0 && maze.StoryStages.Count == 0 && maze.HellParty is null)
        {
            var matching = pool.Where(row => row.Id == declared.Id && row.Path == declared.Path && row.Kind == "[boss]" && row.Layouts == 2).ToArray();
            if (matching.Length == 1)
                return maze with { IsUltimateAltar = true, MapSpecification = [declared with { Kind = "boss" }] };
            return maze;
        }
        // 悲叹之塔的原始BB是单房声明，不把右出口补成网格外房间。
        if (definition.RequiresGriefEntryContract)
        {
            if (!definition.IsGriefTower || maze.Width != 1 || maze.Height != 1 ||
                maze.Starts.Count != 1 || maze.Bosses.Count != 1 || start != new DungeonPoint(0,0) || boss != start ||
                maze.MapSpecification is not null || maze.GreedGrid.Cells is not [{ Index: 1 }] ||
                maze.LayeredMaps.Count != 0 || maze.WarpMaps.Count != 0 || maze.ConditionalMaps.Count != 0 ||
                maze.StoryStages.Count != 0 || maze.BossEntrance is not null || maze.HellParty is not null) return maze;
            // 本版57、58层声明boss，其余98层声明normal；都须核对唯一实际归属，不能只套首层类型。
            var grief = pool.Where(row => row.Kind is "[normal]" or "[boss]" && (row.Layouts & 2) != 0).ToArray();
            if (grief is not [{ Layouts: 2, Dungeons.Length: 1 } candidate] || candidate.Dungeons[0] != definition.Id ||
                !(candidate.Path.StartsWith("map/towerofgrief_down/",StringComparison.OrdinalIgnoreCase) ||
                  candidate.Path.StartsWith("map/towerofgrief_up/",StringComparison.OrdinalIgnoreCase))) return maze;
            // 无专表身份时保留原首层的精确资源约束；其它层必须来自已验证的专表。
            if (definition.GriefFloor is null && (candidate.Kind != "[normal]" || candidate.Id != 100247 ||
                !candidate.Path.Equals("map/towerofgrief_down/100247_001.map",StringComparison.OrdinalIgnoreCase))) return maze;
            var griefRoom = new DungeonMap("boss",start,candidate.Id,candidate.Path);
            return maze with { IsGriefTower = true, GriefRoom = griefRoom, MapSpecification = [griefRoom] };
        }
        // 本版职业体验是无出口的真实单房间；只接受已核验的资源归属、类型、坐标与AA，不放宽普通空格。
        if (definition.IsClassExperience && maze.Width == 1 && maze.Height == 1 && maze.Starts.Count == 1 && maze.Bosses.Count == 1 &&
            start == new DungeonPoint(0,0) && boss == start && maps.Count == 0 && maze.GreedGrid.Cells is [{ Index: 0 }])
        {
            var experience = pool.Where(row => row.Kind == "[normal]" && (row.Layouts & 1) != 0 &&
                row.Path.EndsWith("/76032_changeclass_s_(0,0).map", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (experience.Length != 1) return maze;
            return maze with { IsClassExperience = true, MapSpecification = [new("boss",start,experience[0].Id,experience[0].Path)] };
        }
        // 本版修炼场起点与终点重合，使用实际归属及坐标命名的start资源。
        if (definition.Id == 5000 && maze.Width == 1 && maze.Height == 1 && maze.Starts.Count == 1 && maze.Bosses.Count == 1 &&
            start == new DungeonPoint(0,0) && boss == start && maps.Count == 0 && maze.GreedGrid.Cells is [{ Index: 1 }])
        {
            var training = pool.Where(row => row.Kind == "[normal]" && (row.Layouts & 2) != 0 &&
                row.Path.EndsWith("/11100(0,0)start.map",StringComparison.OrdinalIgnoreCase)).ToArray();
            if (training.Length != 1) return maze;
            return maze with { IsTrainingRoom = true, MapSpecification = [new("boss",start,training[0].Id,training[0].Path)] };
        }
        // 实验性（#259，依据来自 Go 项目反汇编，未经 V31 客户端验证）：比赛副本是 1x1 单图，起点与 Boss 同为 (0,0)，
        // 地图取 PVF 地图反向 [dungeon] 声明的唯一 [boss] 型比赛图（BB BB，布局位 1）。
        if (definition.IsTournament && maze.Width == 1 && maze.Height == 1 && maze.Starts.Count == 1 && maze.Bosses.Count == 1 &&
            start == new DungeonPoint(0,0) && boss == start && maps.Count == 0 && maze.GreedGrid.Cells is [{ Index: 1 }])
        {
            var tournament = pool.Where(row => row.Kind == "[boss]" && (row.Layouts & 2) != 0 &&
                row.Path.StartsWith("map/tournament/",StringComparison.OrdinalIgnoreCase)).ToArray();
            if (tournament.Length != 1) return maze;
            return maze with { IsTournament = true, MapSpecification = [new("boss",start,tournament[0].Id,tournament[0].Path)] };
        }
        var specified = maps.Values.Concat(maze.MapAlternatives).ToDictionary(row => (row.Position,row.Kind));
        int dummyCount = definition.DummyAppearCount;
        bool dummy = dummyCount > 0 && pool.Any(row => row.Kind == "[dummy]");
        var points = maze.GreedGrid.Cells!.Where(cell => cell.Index != 0)
            .Select(cell => new DungeonPoint(cell.SourceOrdinal % maze.Width,cell.SourceOrdinal / maze.Width)).ToHashSet();
        var missingNormal = points.Where(point => !Available(point,"map")).ToArray();
        var stageTargets = bosses.Where(point => points.Contains(point) && Available(point,"boss") && (!dummy || Available(point,"dummy"))).ToArray();
        var initialTargets = stageTargets.Where(point => missingNormal.All(missing => missing == point)).ToArray();
        starts = starts.Where(points.Contains).ToArray();
        if (starts.Length == 0 || initialTargets.Length == 0) return maze;
        start = Choose(starts); boss = Choose(initialTargets);
        var stages = new List<DungeonMap>();
        if (dummy)
        {
            var target = boss;
            for (int index = 0; index <= dummyCount; index++)
            {
                if (index > 0)
                {
                    var remaining = stageTargets.Where(point => point != target).ToArray();
                    target = remaining.Length == 0 ? target : Choose(remaining);
                }
                var stage = Select(target,index == dummyCount ? "boss" : "dummy");
                if (stage is null) return maze;
                stages.Add(stage with { Kind = "boss",DummyStageIndex = index });
            }
        }
        maps.Clear();
        foreach (var cell in maze.GreedGrid.Cells!)
        {
            if (cell.Index == 0) continue;
            var point = new DungeonPoint(cell.SourceOrdinal % maze.Width,cell.SourceOrdinal / maze.Width);
            var selected = dummy && point == boss ? stages[0] : Select(point,point == boss ? "boss" : "map");
            if (selected is null) return maze;
            maps.Add(point,selected);
        }
        var result = maze with { Starts = [start], Bosses = [boss],DummyStages = stages.ToImmutableArray(),MapAlternatives = [],
            MapSpecification = maps.Values.OrderBy(row => row.Position.Y).ThenBy(row => row.Position.X).ToImmutableArray() };
        return result.StaticTopology.Supported ? result : maze;

        DungeonMap? Select(DungeonPoint point,string kind)
        {
            if (point.X < 0 || point.Y < 0 || point.X >= maze.Width || point.Y >= maze.Height) return null;
            var explicitMaps = Explicit(point,kind);
            if (explicitMaps.Length > 0) return Choose(explicitMaps);
            int layout = maze.GreedGrid.Cells![point.Y*maze.Width+point.X].Index;
            string type = kind == "map" ? "[normal]" : kind == "boss" ? "[boss]" : "[dummy]";
            var candidates = pool.Where(row => row.Kind == type && (row.Layouts & (1 << layout)) != 0).ToArray();
            if (candidates.Length == 0)
            {
                // 没有普通模板时复用dummy场景，但普通身份不创建其中的假Boss演员。
                if (!dummy || kind != "map") return null;
                var fallback = Select(point,"dummy");
                return fallback is null ? null : fallback with { Kind = "map" };
            }
            var selected = Choose(candidates);
            return new(kind,point,selected.Id,selected.Path);
        }

        DungeonMap[] Explicit(DungeonPoint point,string kind)
        {
            if (!specified.TryGetValue((point,kind == "dummy" ? "map" : kind),out var source)) return [];
            var ids = source.CandidateIds.Count == 0 ? new[] { source.Id } : source.CandidateIds.Distinct().ToArray();
            return ids.Where(id => kind == "dummy" ? byId.TryGetValue(id,out var dummyRow) && dummyRow.Kind == "[dummy]" :
                    !dummy || kind != "map" || !byId.TryGetValue(id,out var normalRow) || normalRow.Kind == "[normal]")
                .Select(id => source with { Kind = kind,Id = id,
                    Path = byId.TryGetValue(id,out var row) ? row.Path : paths?.GetValueOrDefault(id) ?? (id == source.Id ? source.Path : ""),
                    CandidateIds = source.CandidateIds.Count > 1 ? ImmutableArray.Create(id) : source.CandidateIds }).ToArray();
        }

        bool Available(DungeonPoint point,string kind) => Explicit(point,kind).Length > 0 ||
            pool.Any(row => row.Kind == (kind == "map" ? "[normal]" : kind == "boss" ? "[boss]" : "[dummy]") &&
                point.X >= 0 && point.Y >= 0 && point.X < maze.Width && point.Y < maze.Height &&
                (row.Layouts & (1 << maze.GreedGrid.Cells![point.Y*maze.Width+point.X].Index)) != 0) ||
            dummy && kind == "map" && Available(point,"dummy");

        T Choose<T>(IReadOnlyList<T> rows)
        {
            int index = rows.Count == 1 ? 0 : draw(rows.Count);
            if (index < 0 || index >= rows.Count) throw new InvalidDataException("普通地图随机索引越界");
            return rows[index];
        }
    }

    DungeonMaze ResolveLostLand(DungeonDefinition definition, DungeonMaze maze)
    {
        if (maze.MapSpecification is not { Count: > 0 } declared || maze.Starts.Count != 1 || maze.Bosses.Count != 1 ||
            maze.MapAlternatives.Count != 0 || maze.LayeredMaps.Count != 0 || maze.ConditionalMaps.Count != 0 ||
            maze.StoryStages.Count != 0 || maze.BossEntrance is not null || maze.HellParty is not null)
            return maze;
        var grid = maze.GreedGrid;
        bool InBounds(DungeonPoint point) => point.X >= 0 && point.Y >= 0 && point.X < maze.Width && point.Y < maze.Height;
        var boss = maze.Bosses[0];
        if (!InBounds(boss) || !InBounds(maze.Starts[0]) || maze.Starts[0] == boss) return maze;
        var maps = new Dictionary<DungeonPoint, DungeonMap>();
        foreach (var map in declared)
        {
            if (map.Kind != "map" || !InBounds(map.Position) || !maps.TryAdd(map.Position, map)) return maze;
            IReadOnlyList<int> ids = map.CandidateIds.Count == 0 ? [map.Id] : map.CandidateIds;
            // 显式行由DGN直接声明，不要求MAP具备反向随机池资格；最终候选仍由Catalog复验文件存在。
            string? PathOf(int id) => paths?.GetValueOrDefault(id) ?? byId.GetValueOrDefault(id)?.Path;
            if (!ids.Contains(map.Id) || ids.Any(id => PathOf(id) is null) || PathOf(map.Id) != map.Path) return maze;
        }
        if (maps.ContainsKey(boss) || !maps.ContainsKey(maze.Starts[0])) return maze;
        for (int at = 0; at < grid.Cells!.Count; at++)
        {
            var point = new DungeonPoint(at % maze.Width, at / maze.Width);
            if (grid.Cells[at].Index != 0 && point != boss && !maps.ContainsKey(point)) return maze;
        }
        int layout = grid.Cells[boss.Y * maze.Width + boss.X].Index;
        if (layout == 0) return maze;
        // 02A17ABB优先取Boss容器；这里只补唯一匹配的反向注册，不固定11208。
        var candidates = entries.Where(row => row.Dungeons.Contains(definition.Id) && row.Kind == "[boss]" &&
            (row.Layouts & (1 << layout)) != 0).ToArray();
        if (candidates is not [var candidate]) return maze;
        maps.Add(boss, new("boss", boss, candidate.Id, candidate.Path));
        // AA是显式传送房，来源也必须有真实地图；胶囊的(-1,-1)不能单独证明房间连接。
        foreach (var map in maps.Values)
            if (grid.Cells![map.Position.Y * maze.Width + map.Position.X].Index == 0 &&
                !maze.WarpMaps.Any(warp => warp.Destination == map.Position && warp.Source != map.Position && maps.ContainsKey(warp.Source)))
                return maze;
        return maze with { IsLostLand = true,
            MapSpecification = maps.Values.OrderBy(row => row.Position.Y).ThenBy(row => row.Position.X).ToImmutableArray() };
    }
}
