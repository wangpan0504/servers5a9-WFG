using System.Buffers.Binary;

namespace DfoServer.Network.Builders;

internal static class LoginPackets
{
    // NOTI CHANNELINFO 的 protobuf 包装：u32 长度 + 消息。
    // 023B7870 -> 0041B8A0；field 2/varint 写对象 +40h，唯一必填标志是 has_bits & 2。
    // 握手初始化 14 个 codec，固定读取 334B key。
    // field 3 的长度为 varint CE 02；此兼容数据不用于账号身份或传输保密。
    public static byte[] ChannelInfo()
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write(0); // protobuf 长度在消息写完后回填。
        writer.Write(new byte[] { 0x10, 1, 0x1a });
        writer.Write7BitEncodedInt(334);
        writer.Write(System.Security.Cryptography.RandomNumberGenerator.GetBytes(334));
        // 0041BF07/0041BF59 -> 对象 +78/+7C；0239B280 核对双方完整枚举数量。
        writer.Write7BitEncodedInt(30004 << 3);
        writer.Write7BitEncodedInt((int)CmdPacketType.END);
        writer.Write7BitEncodedInt(30005 << 3);
        writer.Write7BitEncodedInt((int)NotiPacketType.END);
        byte[] body = buffer.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(body, body.Length - 4);
        // 028F8430 的首包分支绕过通用 DecodeBody，0369FA30 按 ROL8(x,6)^B5 解码。
        // 包含长度前缀的整个 body 必须先做逆变换，CRC 则由 GameFrame 对线上字节计算。
        for (int i = 0; i < body.Length; i++)
        {
            int value = body[i] ^ 0xb5;
            body[i] = (byte)(((value >> 6) | (value << 2)) & 0xff);
        }
        return body;
    }
    public static byte[] PrecheckAccepted() => [1];
}
