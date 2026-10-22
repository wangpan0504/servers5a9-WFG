using System.Buffers.Binary;
using DfoServer.Network.Parsers;

namespace DfoServer.Network.Builders;

internal static class StackableActionPackets
{
    // Full03 ACK515 028C0AE0：成功action为u32；失败在公共u16错误后为u8，不能复制旧版错误前缀。
    public static byte[] Used(StackableActionUseRequest request)
    {
        byte[] body = new byte[8]; body[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1),request.Slot); body[3] = request.List;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),request.Action);
        return body;
    }
    public static byte[] Rejected(StackableActionUseRequest request,ushort error = 0)
    {
        byte[] body = new byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1),error);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(3),request.Slot); body[5] = request.List; body[6] = unchecked((byte)request.Action);
        return body;
    }
}
