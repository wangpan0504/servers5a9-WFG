using System.Buffers.Binary;

namespace DfoServer.Network.Builders.Inventory;

internal static class SharedEffectItemPackets
{
    public static byte[] Result(ushort slot, byte outcome, ushort error = 0)
    {
        byte[] body = new byte[error == 0 ? 4 : 3];
        body[0] = error == 0 ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), error == 0 ? slot : error);
        if (error == 0) body[3] = outcome;
        return body;
    }

    public static byte[] Effect(ushort actorId, int itemId, byte outcome)
    {
        byte[] body = new byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(body, actorId);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), itemId);
        body[6] = outcome;
        return body;
    }
}
