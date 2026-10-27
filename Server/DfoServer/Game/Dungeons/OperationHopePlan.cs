using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record OperationHopeMissionBinding(DungeonRunIdentity Identity, int MazeOrdinal, ActiveMainQuest Acceptance, OperationHopeMission Mission)
{
    public PreparedDungeonDeath? TargetDeath { get; init; }
}

// 仅判断资源声明的区域连接，不以此替代正式实例的清图事实或开启任务许可。
internal static class OperationHopePlan
{
    // 用户确认的本服兼容：先按26/100决定隐藏分支，再在该分支内均匀选择一次迷宫。
    public static int ChooseMaze(OperationHopeCatalog catalog,Func<int,int>? draw=null)
    {
        int Sample(int bound)
        {
            int value=(draw??Random.Shared.Next)(bound);
            return value>=0 && value<bound ? value : throw new InvalidDataException("希望迷宫抽样越界");
        }
        bool hidden=Sample(catalog.HiddenDenominator)>=catalog.HiddenDenominator-catalog.HiddenNumerator;
        var candidates=catalog.Mazes.Where(row=>row.Value.HiddenBoss==hidden).OrderBy(row=>row.Key).Select(row=>row.Key).ToArray();
        if (candidates.Length==0) throw new InvalidDataException("希望概率分支缺少迷宫");
        return candidates[Sample(candidates.Length)];
    }

    public static bool CanConnectAreas(OperationHopeCatalog catalog, int sourceMapId, int destinationMapId) =>
        catalog.Maps.TryGetValue(sourceMapId, out var source) &&
        catalog.Maps.TryGetValue(destinationMapId, out var destination) &&
        (source.AreaId == destination.AreaId || source.NextAreas.Contains(destination.AreaId));

    // 普通入场仅允许把原 Boss 坐标的 map 标成 boss；其它身份、候选和几何声明必须原样保留。
    public static bool MatchesLayout(DungeonMaze expected, DungeonMaze actual) =>
        expected.SourceOrdinal == actual.SourceOrdinal && expected.Width == actual.Width && expected.Height == actual.Height && expected.Greed == actual.Greed &&
        expected.Starts.SequenceEqual(actual.Starts) && expected.Bosses.SequenceEqual(actual.Bosses) && actual.QuestConnectionTokens.Count == 0 &&
        expected.MapSpecification is { } original && actual.MapSpecification is { } maps && original.Count == maps.Count &&
        original.Zip(maps).All(pair => pair.First == pair.Second || pair.First.Kind == "map" && pair.Second.Kind == "boss" &&
            expected.Bosses.Contains(pair.First.Position) && pair.Second with { Kind = "map" } == pair.First);

    // 只证明静态声明；实例还必须验证当前房间、清图、epoch与对象归属。
    public static bool IsDeclaredTransition(OperationHopeCatalog catalog, int mazeOrdinal, DungeonPoint source, DungeonPoint destination)
    {
        if (!catalog.Mazes.TryGetValue(mazeOrdinal,out var maze) || source == destination) return false;
        var from = maze.Layout.MapSpecification?.SingleOrDefault(row => row.Position == source);
        var to = maze.Layout.MapSpecification?.SingleOrDefault(row => row.Position == destination);
        if (from is null || to is null || !catalog.Maps.TryGetValue(from.Id,out var fromArea) || !catalog.Maps.TryGetValue(to.Id,out var toArea)) return false;
        if (fromArea.AreaId != toArea.AreaId)
            return fromArea.NextAreas.Contains(toArea.AreaId) && maze.Warps.Any(warp => warp.Source == source && warp.Destination == destination);
        return maze.ForwardLinks.Any(link => link.First == source && link.Second == destination);
    }

    public static bool IsDeclaredTerminal(OperationHopeCatalog catalog, int mazeOrdinal, int mapId) =>
        catalog.Mazes.TryGetValue(mazeOrdinal,out var maze) && maze.ClearMaps.Contains(mapId);

    public static bool IsTerminalArea(OperationHopeCatalog catalog, int mapId) =>
        catalog.Maps.TryGetValue(mapId, out var map) &&
        catalog.Areas[map.AreaId].Maps.All(id => catalog.Maps[id].NextAreas.IsEmpty);

    public static ImmutableArray<int> ReachableMissions(OperationHopeCatalog catalog, int mazeOrdinal)
    {
        if (!catalog.Mazes.TryGetValue(mazeOrdinal,out var maze) || maze.Layout.MapSpecification is not { } maps) return [];
        var visited = new HashSet<DungeonPoint>();
        var pending = new Queue<DungeonPoint>(maze.Layout.Starts);
        while (pending.TryDequeue(out var point))
        {
            if (!visited.Add(point)) continue;
            foreach (var next in maps)
                if (!visited.Contains(next.Position) && IsDeclaredTransition(catalog,mazeOrdinal,point,next.Position)) pending.Enqueue(next.Position);
        }
        var reachableMaps = maps.Where(map => visited.Contains(map.Position)).Select(map => map.Id).ToHashSet();
        return catalog.MissionIds.Where(id => maze.MissionMaps[id].Any(reachableMaps.Contains)).ToImmutableArray();
    }

    // 本服已授权规则：按 PVF 原序冻结合法可达候选，均匀抽一次；调用方随入场事务保存结果，重试不能再调用。
    public static int? ChooseMission(OperationHopeCatalog catalog, int mazeOrdinal, Func<int,int>? draw = null)
    {
        var candidates = ReachableMissions(catalog,mazeOrdinal);
        if (candidates.IsEmpty) return null;
        int selected = draw is null ? Random.Shared.Next(candidates.Length) : draw(candidates.Length);
        if (selected < 0 || selected >= candidates.Length) throw new InvalidOperationException("希望任务抽取越过冻结候选列表");
        return candidates[selected];
    }

    // 将本次已选定任务绑定到原实例身份；不在校验或重试中重新抽取。
    public static OperationHopeMissionBinding? TryBind(OperationHopeCatalog catalog, int chosenQuestId, int mazeOrdinal,
        int level, DungeonRunIdentity identity, ActiveMainQuest acceptance)
    {
        if (identity.InstanceId == Guid.Empty || identity.QuestId != 0 || identity.Epoch < 0 ||
            acceptance.AcceptanceId == Guid.Empty || acceptance.QuestId != chosenQuestId || acceptance.ConditionState != 1 ||
            !catalog.Missions.TryGetValue(chosenQuestId,out var mission) || level < mission.Quest.MinimumLevel || level > mission.Quest.MaximumLevel ||
            !ReachableMissions(catalog,mazeOrdinal).Contains(chosenQuestId)) return null;
        return new(identity,mazeOrdinal,acceptance,mission);
    }

    public static bool Matches(OperationHopeMissionBinding binding, DungeonRunIdentity identity, ActiveMainQuest acceptance) =>
        identity.CharacterId == binding.Identity.CharacterId && identity.InstanceId == binding.Identity.InstanceId &&
        identity.TaskAttempt == binding.Identity.TaskAttempt && identity.QuestId == binding.Identity.QuestId && identity.Epoch >= binding.Identity.Epoch &&
        acceptance.QuestId == binding.Acceptance.QuestId && acceptance.Slot == binding.Acceptance.Slot &&
        acceptance.AcceptanceId == binding.Acceptance.AcceptanceId && acceptance.ConditionState <= 1;

    // 只有已提交并由原 Run 确认的真实怪物死亡才可形成领奖证据。
    public static OperationHopeMissionBinding RecordTarget(OperationHopeMissionBinding binding, SinglePlayerDungeonRun run, PreparedDungeonDeath death)
    {
        if (binding.TargetDeath is not null || run.DungeonId != 8523 || run.LastLoadingFact?.MazeSourceOrdinal != binding.MazeOrdinal || !Matches(binding,run.Snapshot.Identity,binding.Acceptance) ||
            !run.IsConfirmedDeath(death) || !Matches(binding,death.Death.Identity,binding.Acceptance) || death.Monster.AiCharacter is not null || death.Monster.MonsterId != binding.Mission.MonsterId)
            return binding;
        return binding with { TargetDeath = death };
    }

    public static bool CanClaim(OperationHopeCatalog catalog, OperationHopeMissionBinding binding, SinglePlayerDungeonRun run, ActiveMainQuest active)
    {
        var snapshot = run.Snapshot;
        if (snapshot.Phase != SinglePlayerDungeonPhase.Settlement || run.DungeonId != 8523 || run.LastLoadingFact?.MazeSourceOrdinal != binding.MazeOrdinal || !Matches(binding,snapshot.Identity,active) || active.ConditionState != 0 ||
            binding.TargetDeath is not { } death || !run.IsConfirmedDeath(death) || death.Monster.AiCharacter is not null ||
            death.Monster.MonsterId != binding.Mission.MonsterId || !Matches(binding,death.Death.Identity,active) || run.ClearFact is not { DungeonId: 8523, ClearMap: { } map } clear ||
            clear.Identity != snapshot.Identity || !clear.ConfirmedDeathEvents.Contains(death.Death.EventId) ||
            !catalog.Mazes.TryGetValue(binding.MazeOrdinal,out var maze)) return false;
        return IsDeclaredTerminal(catalog,binding.MazeOrdinal,map.Id);
    }

}
