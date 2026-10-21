using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DfoServer.Game.Dungeons;

internal static class DungeonLegendSelection
{
    // 本服以本地自然日和世界地图派生目标；相同资源集合下，重开选图或重启不会重抽。
    public static int DailyIndex(int count, int worldMap, DateTimeOffset localNow)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        Span<byte> input = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(input, DateOnly.FromDateTime(localNow.DateTime).DayNumber);
        BinaryPrimitives.WriteInt32LittleEndian(input[4..], worldMap);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return (int)(BinaryPrimitives.ReadUInt64LittleEndian(hash) % (uint)count);
    }
}
