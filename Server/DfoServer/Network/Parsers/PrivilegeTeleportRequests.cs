using System.Buffers.Binary;
using DfoServer.Game.Town;

namespace DfoServer.Network.Parsers;

internal static class PrivilegeTeleportRequests
{
    public static PrivilegeTeleportRequest Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length != 19) throw new InvalidDataException("礼遇传送请求必须为19字节");
        // 0284FFE8只初始化末六字节；前13字节不参与角色、资格、来源或赛季判断。
        return new(new(body[13], body[14], BinaryPrimitives.ReadUInt16LittleEndian(body[15..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[17..])));
    }
}
