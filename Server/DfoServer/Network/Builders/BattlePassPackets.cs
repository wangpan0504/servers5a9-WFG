using System.Buffers.Binary;

namespace DfoServer.Network.Builders;

internal readonly record struct BattlePassMissionCell(
    ushort Key,
    ushort Count,
    byte State4,
    byte State5);

internal static class BattlePassPackets
{
    public static byte[] PassInfo(
        byte type,
        ReadOnlySpan<byte> reserved3,
        uint level,
        uint exp)
    {
        if (reserved3.Length != 3)
            throw new ArgumentException("Pass info requires exactly three reserved bytes.", nameof(reserved3));

        byte[] payload = new byte[12];
        payload[0] = type;
        reserved3.CopyTo(payload.AsSpan(1, 3));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), level);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), exp);
        return payload;
    }

    public static byte[] Missions(
        ReadOnlySpan<BattlePassMissionCell> firstEight,
        ReadOnlySpan<byte> remaining108)
    {
        if (firstEight.Length != 8)
            throw new ArgumentException("Missions requires exactly eight explicit cells.", nameof(firstEight));
        if (remaining108.Length != 108)
            throw new ArgumentException("Missions requires exactly 108 remaining bytes.", nameof(remaining108));

        byte[] payload = new byte[156];
        for (int i = 0; i < firstEight.Length; i++)
        {
            BattlePassMissionCell cell = firstEight[i];
            Span<byte> slot = payload.AsSpan(i * 6, 6);
            BinaryPrimitives.WriteUInt16LittleEndian(slot[..2], cell.Key);
            BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(2, 2), cell.Count);
            slot[4] = cell.State4;
            slot[5] = cell.State5;
        }
        // 后108B的消费者语义尚未确认，只保存调用者显式提供的原字节。
        remaining108.CopyTo(payload.AsSpan(48, 108));
        return payload;
    }
}
