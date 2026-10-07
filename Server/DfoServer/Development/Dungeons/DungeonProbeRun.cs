using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Development.Dungeons;

internal enum DungeonDeathReport { Rejected, Duplicate, Accepted }

// 独立实例的客户端联调状态，不作为服务端伤害、任务条件、掉落或经验的权威事实。
internal sealed class DungeonProbeRun
{
    private sealed class Room(DungeonMonsterPlan monsters)
    {
        public DungeonMonsterPlan Monsters { get; } = monsters;
        public HashSet<ushort> ReportedDead { get; } = [];
    }
    readonly Dictionary<DungeonPoint, Room> rooms = [];
    readonly HashSet<ushort> allocatedKeys = [];
    Room current;
    public Guid InstanceId { get; } = Guid.NewGuid();
    public long SelectionEpoch { get; }
    public DungeonEntryPlan Entry { get; }
    public DungeonMap CurrentMap => current.Monsters.Room.Map;
    public int RoomRevision { get; private set; } = 1;
    public bool Loading { get; private set; } = true;
    public bool Ended { get; private set; }
    public bool AllReportedDead => current.ReportedDead.Count == current.Monsters.Monsters.Count;

    public DungeonProbeRun(DungeonEntryPlan entry, DungeonMonsterPlan monsters, long epoch)
    {
        if (entry.StartMap != monsters.Room.Map) throw new InvalidDataException("初始怪物与房间不匹配");
        Entry = entry;
        SelectionEpoch = epoch;
        current = new(monsters);
        AddRoom(current);
    }

    public bool Matches(ushort characterId, long revision, long epoch) =>
        !Ended && Entry.CharacterId == characterId && Entry.CharacterRevision == revision && SelectionEpoch == epoch;

    public void Loaded() => Loading = false;
    public void End() => Ended = true;

    public DungeonDeathReport ReportDeath(uint key)
    {
        if (Ended || Loading || key > ushort.MaxValue || !current.Monsters.Monsters.Any(monster => monster.ObjectKey == key))
            return DungeonDeathReport.Rejected;
        return current.ReportedDead.Add((ushort)key) ? DungeonDeathReport.Accepted : DungeonDeathReport.Duplicate;
    }

    public DungeonMap? Target(byte x, byte y)
    {
        if (Ended || Loading || !AllReportedDead || Math.Abs(CurrentMap.Position.X - x) + Math.Abs(CurrentMap.Position.Y - y) != 1)
            return null;
        return Entry.Maze.MapSpecification?.SingleOrDefault(map => map.Position == new DungeonPoint(x, y));
    }

    public DungeonMonsterPlan? Cached(DungeonMap map) => rooms.TryGetValue(map.Position, out var room) && room.Monsters.Room.Map == map
        ? room.Monsters with { Monsters = Array.AsReadOnly(room.Monsters.Monsters.Where(monster => !room.ReportedDead.Contains(monster.ObjectKey)).ToArray()) }
        : null;

    public void Enter(DungeonMap map, DungeonMonsterPlan monsters)
    {
        if (Target(checked((byte)map.Position.X), checked((byte)map.Position.Y)) != map || monsters.Room.Map != map)
            throw new InvalidDataException("诊断过房条件已失效");
        if (!rooms.TryGetValue(map.Position, out var next))
        {
            next = new(monsters);
            AddRoom(next);
        }
        current = next;
        RoomRevision++;
        Loading = true;
    }

    void AddRoom(Room room)
    {
        ushort[] keys = room.Monsters.Monsters.Select(monster => monster.ObjectKey).ToArray();
        if (keys.Distinct().Count() != keys.Length || keys.Any(key => key == Entry.CharacterId || allocatedKeys.Contains(key)))
            throw new InvalidDataException("诊断怪物 key 重复或与角色冲突");
        foreach (ushort key in keys) allocatedKeys.Add(key);
        rooms.Add(room.Monsters.Room.Map.Position, room);
    }
}
