using System.Buffers.Binary;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Builders;

// 首版显示适配；不复用第三版次数/榜单通知，不参与奖励提交。
internal static class BeastDisplayPackets
{
    internal const ushort Opcode = 0x7FF1;
    internal const int Size = 144;
    internal static readonly uint[] Rewards = [10303240, 10303242, 10303243, 10303244, 10303245, 10303246];

    internal static byte[] Selection(uint serial, Guid connection, ushort character, int day,
        ReadOnlySpan<(uint Dungeon, byte Remaining)> counts)
    {
        byte[] body = Header(serial, connection, character, 1);
        body[41] = 2;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(44), day);
        for (int i = 0; i < 2; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(48 + i * 4), checked((ushort)counts[i].Dungeon));
            body[50 + i * 4] = counts[i].Remaining;
        }
        return body;
    }

    internal static byte[] Progress(uint serial, Guid connection, ushort character, BeastProgressView view)
    {
        byte[] body = Header(serial, connection, character, 2);
        body[5] = checked((byte)view.Terminal);
        body[6] = view.Stage < 0 ? byte.MaxValue : checked((byte)view.Stage);
        body[7] = checked((byte)view.CompletedStages);
        view.InstanceId.TryWriteBytes(body.AsSpan(16, 16));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(32), view.RemainingMilliseconds);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(36), view.Score);
        body[40] = view.Rank;
        foreach (var stage in view.Stages)
        {
            var row = body.AsSpan(56 + stage.Stage * 12, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(row, Rewards[stage.Stage]);
            BinaryPrimitives.WriteInt32LittleEndian(row[4..], stage.Seconds);
            BinaryPrimitives.WriteInt32LittleEndian(row[8..], stage.Score);
        }
        return body;
    }

    internal static byte[] Clear(uint serial, Guid connection, ushort character) => Header(serial, connection, character, 0);

    static byte[] Header(uint serial, Guid connection, ushort character, byte kind)
    {
        if (serial == 0) throw new ArgumentOutOfRangeException(nameof(serial));
        byte[] body = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0x31565342); // BSV1
        body[4] = kind;
        body[6] = byte.MaxValue;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), character);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), serial);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(32), -1);
        body[40] = 6;
        connection.TryWriteBytes(body.AsSpan(128, 16));
        return body;
    }
}
