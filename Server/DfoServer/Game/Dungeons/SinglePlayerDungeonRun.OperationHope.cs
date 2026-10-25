using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed partial class SinglePlayerDungeonRun
{
    public OperationHopeCatalog? HopeCatalog { get; private set; }
    public OperationHopeMissionBinding? HopeBinding { get; private set; }
    public int? HopeAreaId => HopeCatalog is { } catalog && catalog.Maps.TryGetValue(current.Plan.Room.Map.Id,out var map) ? map.AreaId : null;

    // 与原房间缓存共享加载事实，不另建一套区域访问状态。
    public bool HasLoadedHopeArea(int areaId) => HopeCatalog is { } catalog && rooms.Values.Any(room => room.Loaded &&
        catalog.Maps.TryGetValue(room.Plan.Room.Map.Id,out var map) && map.AreaId == areaId);

    // 仅在任何加载前固定一次；重新接取或换实例不能替换已经运行的任务。
    public bool ConfigureHope(OperationHopeCatalog catalog, OperationHopeMissionBinding binding)
    {
        if (Phase != SinglePlayerDungeonPhase.Loading || LastLoadingFact is not null || binding.Identity != identity ||
            binding.Acceptance.AcceptanceId == Guid.Empty || binding.Acceptance.ConditionState != 1 || entry.DungeonId != 8523 || entry.Difficulty != catalog.Difficulty || !entry.IsOrdinary || entry.IsPractice || entry.IsQuestReplay ||
            binding.MazeOrdinal != entry.Maze.SourceOrdinal || !catalog.Mazes.TryGetValue(binding.MazeOrdinal,out var maze) ||
            !ReferenceEquals(catalog.Missions.GetValueOrDefault(binding.Acceptance.QuestId),binding.Mission) ||
            !OperationHopePlan.MatchesLayout(maze.Layout,entry.Maze) ||
            !maze.Layout.Starts.Contains(entry.StartMap.Position) || !catalog.Maps.ContainsKey(entry.StartMap.Id) ||
            entry.QuestAcceptances.Count(row => row.Slot == binding.Acceptance.Slot && row.QuestId == binding.Acceptance.QuestId && row.AcceptanceId == binding.Acceptance.AcceptanceId && row.ConditionState == 1) != 1)
            return false;
        if (HopeCatalog is not null) return ReferenceEquals(HopeCatalog,catalog) && HopeBinding == binding;
        HopeCatalog = catalog;
        HopeBinding = binding;
        return true;
    }

    private bool CanMoveHopeOrOrdinary(DungeonPoint target) => HopeCatalog is { } catalog && HopeBinding is { } binding
        ? OperationHopePlan.IsDeclaredTransition(catalog,binding.MazeOrdinal,current.Plan.Room.Map.Position,target)
        : connections.Contains((current.Plan.Room.Map.Position,target)) || CanRidableWarp(target) || CanBorderTownWarp(target);

    private bool IsHopeTerminalRoom() => HopeCatalog is { } catalog && HopeBinding is { } binding &&
        OperationHopePlan.IsDeclaredTerminal(catalog,binding.MazeOrdinal,current.Plan.Room.Map.Id) &&
        current.Plan.Monsters.Any(monster => monster.IsBoss && monster.AiCharacter is null && monster.CanReportDeath);

    private void RecordHopeTarget(PreparedDungeonDeath prepared)
    {
        if (HopeBinding is { } binding) HopeBinding = OperationHopePlan.RecordTarget(binding,this,prepared);
    }
}
