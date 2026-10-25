using System.Collections.Immutable;
using PvfLib;
using DfoServer.GameWorld.Quests;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record OperationHopeMap(int Id, int AreaId, ImmutableArray<int> NextAreas);
// 原生 grade29 默认 bit17 等同 not give exp quest；当前完整字段门禁拒绝默认属性清除与反向覆盖。
internal sealed record OperationHopeMission(StoryQuest Quest, int AreaId, int MonsterId, int RewardItemId, int RewardCount, bool SuppressExperience)
{
    public string ExperienceRule => "op-hope-grade29-not-give-exp-v1";
}
internal sealed record OperationHopeMaze(DungeonMaze Layout, bool HiddenBoss, ImmutableArray<int> ClearMaps, int DeclaredCount, ImmutableArray<DungeonWarpMap> Warps, ImmutableDictionary<int, ImmutableArray<int>> MissionMaps, ImmutableArray<DungeonStaticLink> ForwardLinks);
internal sealed record OperationHopeArea(int Id, int QuestId, int MissionValue, ImmutableArray<int> Maps);

// 原始区域图是共享只读资源；不持有玩家任务、当前区域或领奖状态。
internal sealed class OperationHopeCatalog
{
    public const string ResourcePath = "etc/dungeonetc/operationhopedungeoninfo.etc";
    public ImmutableDictionary<int, OperationHopeArea> Areas { get; }
    public ImmutableDictionary<int, OperationHopeMap> Maps { get; }
    public ImmutableArray<int> MissionIds { get; }
    public int FatigueValue { get; }
    public int NeedFatigue { get; }
    public byte Difficulty { get; }
    public ImmutableDictionary<int, OperationHopeMaze> Mazes { get; }
    public ImmutableDictionary<int, OperationHopeMission> Missions { get; }

    private OperationHopeCatalog(OperationHopeFile source, OperationHopeDungeonFile dungeon, DungeonDefinition definition, QuestCatalog quests, DungeonRoomCatalog rooms)
    {
        if (source.MissionIds.IsEmpty || source.MissionIds.Any(id => id <= 0) ||
            source.MissionIds.Distinct().Count() != source.MissionIds.Length || source.Areas.IsEmpty ||
            source.FatigueValue < 0 || source.NeedFatigue < 0)
            throw new InvalidDataException("代号希望任务列表或疲劳声明无效");
        if (!definition.TryGetDesignatedDifficulty(out byte difficulty))
            throw new InvalidDataException("代号希望缺少唯一指定难度");
        Difficulty = difficulty;
        var areas = ImmutableDictionary.CreateBuilder<int, OperationHopeArea>();
        var maps = ImmutableDictionary.CreateBuilder<int, OperationHopeMap>();
        foreach (var area in source.Areas)
        {
            if (area.Id < 0 || area.Maps.IsEmpty || !source.MissionIds.Contains(area.QuestId) ||
                !areas.TryAdd(area.Id, new(area.Id, area.QuestId, area.MissionValue, area.Maps.Select(row => row.MapId).ToImmutableArray())))
                throw new InvalidDataException("代号希望区域重复、缺少地图或引用未知任务");
            foreach (var map in area.Maps)
            {
                if (map.MapId <= 0 || map.NextAreas.Distinct().Count() != map.NextAreas.Length ||
                    !maps.TryAdd(map.MapId, new(map.MapId, area.Id, map.NextAreas)))
                    throw new InvalidDataException("代号希望地图重复或路线声明无效");
            }
        }
        if (areas.Values.Select(row => row.QuestId).Distinct().Count() != source.MissionIds.Length ||
            maps.Values.Any(map => map.NextAreas.Any(id => id == map.AreaId || !areas.ContainsKey(id))))
            throw new InvalidDataException("代号希望任务或区域链接不完整");
        Areas = areas.ToImmutable();
        Maps = maps.ToImmutable();
        MissionIds = source.MissionIds;
        FatigueValue = source.FatigueValue;
        NeedFatigue = source.NeedFatigue;
        if (definition.Id != 8523 || dungeon.ContinuousDungeon != 1 || dungeon.Mazes.Length != definition.Mazes.Count || dungeon.Warps.IsEmpty)
            throw new InvalidDataException("代号希望迷宫与副本目录不一致");
        var missions = ImmutableDictionary.CreateBuilder<int, OperationHopeMission>();
        foreach (var area in Areas.Values)
        {
            var quest = quests.Get(area.QuestId);
            if (!SupportsMission(quest)) throw new InvalidDataException("代号希望任务声明包含尚未支持的资格或奖励域");
            var item = quest.Rewards[0];
            var count = quest.Rewards[1];
            missions.Add(quest.Id,new(quest,area.Id,quest.IntData[2],item.Value,count.Value,true));
        }
        Missions = missions.ToImmutable();
        var roomMonsters = new Dictionary<int, HashSet<int>>();
        var mazes = ImmutableDictionary.CreateBuilder<int, OperationHopeMaze>();
        foreach (var raw in dungeon.Mazes)
        {
            var layout = definition.Mazes.Single(row => row.SourceOrdinal == raw.Ordinal);
            if (layout.MapSpecification is not { Count: > 0 } mapRows) throw new InvalidDataException("代号希望迷宫缺少显式地图");
            var positions = mapRows.Select(row => row.Position).ToHashSet();
            var warps = dungeon.Warps.Select(row => new DungeonWarpMap(new(row.SourceX,row.SourceY),new(row.DestinationX,row.DestinationY))).ToImmutableArray();
            if (raw.ClearMaps.IsEmpty || raw.ClearMaps.Any(id => id <= 0) || raw.ClearMaps.Distinct().Count() != raw.ClearMaps.Length ||
                !mapRows.Any(row => raw.ClearMaps.Contains(row.Id)) ||
                warps.Distinct().Count() != warps.Length || warps.Any(warp => !positions.Contains(warp.Source) || !positions.Contains(warp.Destination)))
                throw new InvalidDataException("代号希望通关图或全局传送没有对应迷宫格");
            var missionMaps = ImmutableDictionary.CreateBuilder<int, ImmutableArray<int>>();
            foreach (var map in mapRows.Where(row => Maps.ContainsKey(row.Id)))
                if (!roomMonsters.ContainsKey(map.Id))
                    roomMonsters.Add(map.Id,rooms.Get(map,requirePassiveDefinitions:false,allowUnresolvedSpecialMetadata:true).Monsters.Select(row => row.MonsterId).ToHashSet());
            foreach (var mission in Missions.Values)
                missionMaps.Add(mission.Quest.Id,mapRows.Where(map => Maps.TryGetValue(map.Id,out var area) && area.AreaId == mission.AreaId &&
                    roomMonsters[map.Id].Contains(mission.MonsterId)).Select(map => map.Id).ToImmutableArray());
            var positionsByArea = mapRows.Where(map => Maps.ContainsKey(map.Id)).ToDictionary(map => map.Position,map => Maps[map.Id].AreaId);
            var links = layout.StaticTopology.Links ?? throw new InvalidDataException("代号希望迷宫静态拓扑无效");
            var sameAreaLinks = links.Where(link => positionsByArea.TryGetValue(link.First,out int first) &&
                positionsByArea.TryGetValue(link.Second,out int second) && first == second).ToArray();
            var depth = new Dictionary<DungeonPoint,int>();
            var pending = new Queue<DungeonPoint>();
            foreach (var start in layout.Starts.Concat(warps.Select(warp => warp.Destination)))
                if (positionsByArea.ContainsKey(start) && depth.TryAdd(start,0)) pending.Enqueue(start);
            while (pending.TryDequeue(out var point))
                foreach (var link in sameAreaLinks.Where(link => link.First == point || link.Second == point))
                {
                    var next = link.First == point ? link.Second : link.First;
                    if (depth.TryAdd(next,depth[point]+1)) pending.Enqueue(next);
                }
            var forward = ImmutableArray.CreateBuilder<DungeonStaticLink>();
            foreach (var link in sameAreaLinks)
            {
                if (!depth.TryGetValue(link.First,out int first) || !depth.TryGetValue(link.Second,out int second) || Math.Abs(first-second) != 1)
                    throw new InvalidDataException("代号希望同区边无法按入口解释为单向路线");
                forward.Add(first < second ? link : new(link.Second,link.First));
            }
            mazes.Add(raw.Ordinal,new(layout,raw.HiddenBoss,raw.ClearMaps,raw.DeclaredCount,warps,missionMaps.ToImmutable(),forward.ToImmutable()));
        }
        Mazes = mazes.ToImmutable();
    }

    // 当前九条任务的完整声明边界；未来附加资格、邮寄或奖励不能被两整数物品对悄悄吞掉。
    internal static bool SupportsMission(StoryQuest quest)
    {
        string[] fields = ["grade", "difficulty", "npc index", "complete npc index", "job", "grow type", "level", "type", "sub type",
            "int data", "reward type", "reward int data", "name", "condition message", "substitutive names"];
        return quest.Grade == "[op hope mission]" && quest.RewardType == "[operation hope]" && quest.Condition == "[hunt monster]" &&
            quest.SubType == -1 && quest.IntData is [8523,-1, > 0,1] && quest.MinimumLevel >= 1 && quest.MaximumLevel >= quest.MinimumLevel &&
            quest.Npc > 0 && quest.CompleteNpc == -1 && quest.Prerequisites.Count == 0 && quest.Attributes.Count == 0 && quest.Difficulty is not null &&
            quest.Rewards is [{ Type: 0, Value: > 0 }, { Type: 0, Value: > 0 }] &&
            quest.Definition is { Job: "[all]", GrowType: -1, Diagnostics.Length: 0 } definition &&
            definition.Fields.All(field => fields.Contains(field.Name) && field.Children.IsEmpty) &&
            definition.Fields.Select(field => field.Name).Distinct().Count() == definition.Fields.Length;
    }

    public static OperationHopeCatalog Load(PvfArchive pvf, DungeonCatalog dungeons, QuestCatalog quests, DungeonRoomCatalog rooms)
    {
        var definition = dungeons.Get(8523);
        return new(OperationHopeFile.Parse(pvf.ReadTokens(ResourcePath),pvf.ResolveString),
            OperationHopeDungeonFile.Parse(pvf.ReadTokens(definition.Path),pvf.ResolveString),definition,quests,rooms);
    }
}
