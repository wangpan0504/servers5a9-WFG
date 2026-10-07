using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DfoServer.Network.Parsers;

internal static class ClientBodyChecksum
{
    public static uint Compute(ReadOnlySpan<byte> body)
    {
        // 匹配004280E0按固定索引折叠MD5的16B输出；不包含末4B检查值或帧头。
        // C2S38/39共用此正文完整性公式，它不是身份签名、服务端HP模拟或死亡事实。
        Span<byte> digest = stackalloc byte[16];
        MD5.HashData(body, digest);
        Span<byte> checksum = stackalloc byte[4];
        checksum[0] = (byte)(digest[0] ^ digest[13] ^ digest[8] ^ digest[10] ^ 0x81);
        checksum[1] = (byte)(digest[1] ^ digest[9] ^ digest[5] ^ digest[12] ^ 0x78);
        checksum[2] = (byte)(digest[2] ^ digest[6] ^ digest[14] ^ digest[4] ^ 0x1A);
        checksum[3] = (byte)(digest[3] ^ digest[15] ^ digest[11] ^ digest[7] ^ 0xBF);
        return BinaryPrimitives.ReadUInt32LittleEndian(checksum);
    }
}
