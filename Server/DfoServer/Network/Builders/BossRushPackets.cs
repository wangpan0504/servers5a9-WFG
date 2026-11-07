using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Builders;

internal static class BossRushPackets
{
    public const byte ResultPlaceholder = 1;

    public static byte[] Data(byte rank, int score, int remainMilliseconds)
    {
        if (rank > 4 || remainMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(rank));
        byte[] body = new byte[9];
        body[0] = rank;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), score);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5), remainMilliseconds);
        return body;
    }

    public static byte[] Timeout(int seconds)
    {
        if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        byte[] body = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(body, seconds);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 1);
        return body;
    }

    public static byte[] DungeonInfo(ImmutableArray<BossRushRoomInfo> rooms)
    {
        if (rooms.IsDefaultOrEmpty) throw new InvalidDataException("BossRush 房间名单为空");
        byte[] body = new byte[4 + 9 * rooms.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body, rooms.Length);
        for (int i = 0; i < rooms.Length; i++)
        {
            if (rooms[i].MonsterId < 0 || rooms[i].SoulType > 2) throw new InvalidDataException("BossRush 房间名单非法");
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4 + i * 9), rooms[i].MonsterId);
            body[8 + i * 9] = rooms[i].SoulType;
        }
        return body;
    }

    public static byte[] Result(byte rank, int score, int itemId = 0, int count = 0)
    {
        bool rewarded = itemId > 0 && count > 0;
        byte[] body = new byte[rewarded ? 15 : 7];
        body[0] = ResultPlaceholder;
        body[1] = rank;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), score);
        body[6] = rewarded ? (byte)1 : (byte)0;
        if (!rewarded) return body;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(7), itemId);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(11), count);
        return body;
    }
}
