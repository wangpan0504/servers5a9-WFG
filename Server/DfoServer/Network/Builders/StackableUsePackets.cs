using System.Buffers.Binary;

namespace DfoServer.Network.Builders;

internal static class StackableUsePackets
{
    // 02356A00：公共状态后必读u16/u8/u32/u32；两个u32回显请求令牌与模板，不是使用次数。
    public static byte[] Result(ushort slot, byte list, uint token, int itemId, ushort error)
    {
        var body = new byte[12]; body[0] = error == 0 ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), error == 0 ? slot : error);
        body[3] = list;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), token);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), checked((uint)itemId));
        return body;
    }
}
