using DfoServer.GameWorld.Dungeons;
using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

internal readonly record struct BossRushHud(byte Rank, int Score, int RemainMilliseconds);
internal readonly record struct BossRushRoomInfo(int MonsterId, byte SoulType);

internal sealed class BossRushRunState(BossRushCatalog catalog, int timeoutMilliseconds)
{
    readonly HashSet<int> scored = [];
    public BossRushCatalog Catalog { get; } = catalog;
    public int TimeoutMilliseconds { get; } = timeoutMilliseconds > 0 ? timeoutMilliseconds :
        throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
    public int Score { get; private set; }
    public byte Rank => Catalog.RankOf(Score);

    public bool TryScore(int monsterId)
    {
        int index = Catalog.IndexOf(monsterId);
        if (index < 0 || !scored.Add(monsterId)) return false;
        Score = checked(Score + Catalog.SoulFor(index));
        return true;
    }

    public int RemainMilliseconds(long startedTimestamp)
    {
        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        if (elapsed <= 0) return TimeoutMilliseconds;
        return elapsed >= TimeoutMilliseconds ? 0 : TimeoutMilliseconds - (int)elapsed;
    }

    public BossRushHud Hud(long startedTimestamp) => new(Rank, Score, RemainMilliseconds(startedTimestamp));

    public ImmutableArray<BossRushRoomInfo> RoomInfo(DungeonMaze maze, DungeonFixedRoute route)
    {
        if (maze.Width <= 0 || maze.Height <= 0) throw new InvalidDataException("BossRush 网格尺寸非法");
        var rows = new BossRushRoomInfo[checked(maze.Width * maze.Height)];
        var occupied = new HashSet<int>();
        foreach (var room in route.Rooms)
        {
            var point = room.Room.Map.Position;
            if (point.X < 0 || point.X >= maze.Width || point.Y < 0 || point.Y >= maze.Height)
                throw new InvalidDataException("BossRush 房间超出网格");
            int slot = point.Y * maze.Width + point.X;
            if (!occupied.Add(slot)) throw new InvalidDataException("BossRush 房间坐标重复");
            var monsters = room.Monsters.Where(monster => Catalog.IndexOf(monster.MonsterId) >= 0).ToArray();
            if (monsters.Length > 1) throw new InvalidDataException("BossRush 每格只能对应一只计分怪");
            if (monsters.Length == 0) continue;
            int index = Catalog.IndexOf(monsters[0].MonsterId);
            byte type = index < Catalog.BossTypeCount ? (byte)2 :
                index < Catalog.BossTypeCount + Catalog.NamedTypeCount ? (byte)1 : (byte)0;
            rows[slot] = new(monsters[0].MonsterId, type);
        }
        return [.. rows];
    }
}
