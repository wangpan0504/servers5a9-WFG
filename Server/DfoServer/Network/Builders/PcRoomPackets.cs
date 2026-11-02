using System.Buffers.Binary;

namespace DfoServer.Network.Builders;

internal static class PcRoomPackets
{
    // 026E91E0：首DWORD尚无业务解释，保留0；末BYTE是已领奖状态。
    public static byte[] Login(bool claimed) => [0, 0, 0, 0, claimed ? (byte)1 : (byte)0];
    public static byte[] Rental(bool claimed) => [claimed ? (byte)1 : (byte)0];
    public static byte[] Claim(uint error)
    {
        byte[] body = new byte[error == 0 ? 5 : 7];
        body[0] = error == 0 ? (byte)1 : (byte)0;
        // 028F8651先消费共用error16，1550/1637各自回调才读取result32。
        if (error != 0) BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), checked((ushort)error));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(error == 0 ? 1 : 3), error);
        return body;
    }
    // 012FE170：成功后的BYTE是扣除旧物的条数，新物品只由NOTI14发放。
    public static byte[] Weapons(ushort error)
    {
        if (error == 0) return [1, 0];
        byte[] body = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), error);
        return body;
    }
}
