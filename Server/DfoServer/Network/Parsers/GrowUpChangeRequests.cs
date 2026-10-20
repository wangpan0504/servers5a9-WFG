using System.Buffers.Binary;
using DfoServer.Game.Characters;

namespace DfoServer.Network.Parsers;

internal static class GrowUpChangeRequests
{
    public const int BodyLength = 15;

    // 0129CEF1/0129D419 经 03BDF2C0 原样写入15B后直接由 03BE0FF0 发送：前13B未初始化，offset13目标，offset14确认。
    // 静态原码不追加检查尾；另接受与其他请求同公式的4B检查尾，但只在完全匹配时接受。
    public static GrowUpChangeRequest Read(ReadOnlySpan<byte> body)
    {
        bool? checksum = null;
        if (body.Length == BodyLength + 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(body[BodyLength..]) != ClientBodyChecksum.Compute(body[..BodyLength]))
                throw new InvalidDataException("职业变更检查尾不匹配");
            checksum = true;
        }
        else if (body.Length != BodyLength)
            throw new InvalidDataException("职业变更正文必须是15字节");
        if (body[14] > 1) throw new InvalidDataException("职业变更确认标记无效");
        return new(body[13], body[14] == 1, checksum);
    }
}
