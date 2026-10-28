using System.Buffers.Binary;
using System.Collections.Immutable;

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

    public static byte[] DungeonInfo(ImmutableArray<int> bosses)
    {
        if (bosses.IsDefaultOrEmpty) throw new InvalidDataException("BossRush 名单为空");
        byte[] body = new byte[4 + 9 * bosses.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body, bosses.Length);
        for (int i = 0; i < bosses.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4 + i * 9), bosses[i]);
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
