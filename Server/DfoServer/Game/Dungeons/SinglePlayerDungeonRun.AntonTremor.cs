using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed partial class SinglePlayerDungeonRun
{
    readonly (Room Room, ushort Key)[]? antonTremorTargets;

    // 震颤A/B的真正战斗目标在左右支路；上方69308位于场景外，不能要求玩家再击杀该装置。
    // 双精英死亡只开放终点；炮击销毁15467后沿原生物件39提交清图，不由加载提前许可菜单。
    (Room Room, ushort Key)[]? ResolveAntonTremorTargets()
    {
        if (entry.RaidEntry is not { Kind: RaidKind.Anton, Phase: 0 } || entry.DungeonId is not (212 or 214) ||
            entry.Maze.LayeredMaps.Count != 0 || entry.Maze.ConditionalMaps.Count != 0 ||
            entry.Maze.StoryStages.Count != 0 || entry.Maze.DummyStages.Count != 0 || allRooms.Length != 4)
            return null;
        int first = entry.DungeonId == 212 ? 32220 : 32240;
        if (!allRooms.Select(room => room.Plan.Room.Map.Id).Order().SequenceEqual(Enumerable.Range(first, 4)) ||
            entry.BossMap.Id != first + 2 || entry.BossMap.Position != new DungeonPoint(1, 0) ||
            rooms[entry.BossMap.Position].Plan.Monsters.Where(monster => monster.CanReportDeath).ToArray() is not
                [{ MonsterId: 69308, IsBoss: true }]) return null;
        var result = new List<(Room, ushort)>();
        foreach (var (map, monsterId, position) in new[]
            { (first, 59055, new DungeonPoint(2, 1)), (first + 1, 56635, new DungeonPoint(0, 1)) })
        {
            var room = allRooms.Single(row => row.Plan.Room.Map.Id == map);
            if (room.Plan.Room.Map.Position != position || room.Plan.Monsters.Where(monster => monster.CanReportDeath).ToArray() is not
                [var target] || target.MonsterId != monsterId || target.IsFriendly || !target.IsBlocking || target.AiCharacter is not null)
                return null;
            result.Add((room, target.ObjectKey));
        }
        return result.ToArray();
    }

    bool AntonTremorTargetsCommitted => antonTremorTargets is { } targets &&
        targets.All(target => target.Room.Loaded && target.Room.Dead.Contains(target.Key));

    internal bool HasCommittedAntonTremorClear => ClearFact is not null &&
        Phase == SinglePlayerDungeonPhase.Settlement && IsTerminalRoom() && AntonTremorTargetsCommitted && HasCommittedPassiveClearCondition;

    internal bool IsAntonTremor => antonTremorTargets is not null;
}
