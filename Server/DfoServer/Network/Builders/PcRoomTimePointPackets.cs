using System.Buffers.Binary;

namespace DfoServer.Network.Builders;

internal static class PcRoomTimePointPackets
{
    // V31 NOTI561：022007F0固定读取4+4+1+4+4B，视觉组和累计领取位不能交换。
    public static byte[] Progress(uint seconds, uint days, byte dailyMask, uint visualGroup, uint periodMask)
    {
        byte[] body = new byte[17];
        BinaryPrimitives.WriteUInt32LittleEndian(body, seconds);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), days);
        body[8] = dailyMask;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), visualGroup);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), periodMask);
        return body;
    }
}
