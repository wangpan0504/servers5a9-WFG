using System.Buffers.Binary;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Builders;

internal static class FiendwarKeyLeasePackets
{
    internal const ushort Opcode = 0x7FF3;
    internal static byte[] Build(FiendwarKeyLeaseMessage message)
    {
        if (message.Keys.IsDefault || message.Keys.Length > FiendwarKeyLeaseMessage.BatchCapacity)
            throw new InvalidDataException("漩涡对象键通知容量非法");
        var body = new byte[80 + message.Keys.Length * 2];
        var span = body.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x314C5746);
        span[4] = (byte)message.Kind;
        span[5] = message.Status;
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], checked((ushort)message.Keys.Length));
        message.Handshake.TryWriteBytes(span[8..24]);
        message.Scene.Run.TryWriteBytes(span[24..40]);
        BinaryPrimitives.WriteUInt64LittleEndian(span[40..], message.Lease);
        BinaryPrimitives.WriteUInt32LittleEndian(span[48..], message.Scene.Dungeon);
        BinaryPrimitives.WriteUInt32LittleEndian(span[52..], message.Scene.Map);
        BinaryPrimitives.WriteUInt32LittleEndian(span[56..], message.Scene.Seed);
        BinaryPrimitives.WriteUInt32LittleEndian(span[60..], message.Scene.Position);
        BinaryPrimitives.WriteUInt32LittleEndian(span[64..], message.Scene.Serial);
        BinaryPrimitives.WriteInt64LittleEndian(span[68..], message.Scene.Epoch);
        for (int index = 0; index < message.Keys.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(span[(80 + index * 2)..], message.Keys[index]);
        return body;
    }
}
