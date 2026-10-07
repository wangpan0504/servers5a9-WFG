using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace PvfLib.Format;

internal static class PvfCodec
{
    internal const int MaxBytes = 512 * 1024 * 1024;

    internal static void Crypt(Span<byte> bytes, string key, bool protectedKey, uint magic = 0x269EC3)
    {
        unchecked
        {
            uint factor = protectedKey ? 0x393u : 0x1C1u;
            uint seed = (protectedKey ? 0x339E9711u : 0x76826701u) * key[0] + factor * (key[3] + factor * (key[2] + factor * key[1]));
            for (int pos = 0; pos < bytes.Length; pos += 4)
            {
                uint first = 0x343FD * seed + magic;
                seed = 0x343FD * first + magic;
                uint mask = (first & 0xFFFF0000) + (seed >> 16);
                if (bytes.Length - pos >= 4)
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes[pos..], BinaryPrimitives.ReadUInt32LittleEndian(bytes[pos..]) ^ mask);
                else
                    for (int i = 0; i < bytes.Length - pos; i++)
                        bytes[pos + i] ^= (byte)(mask >> (8 * i));
            }
        }
    }

    internal static byte[] Inflate(byte[] compressed, int? expected)
    {
        using var source = new MemoryStream(compressed, false);
        using var input = new ZLibStream(source, CompressionMode.Decompress);
        if (expected is int size)
        {
            if (size < 0 || size > MaxBytes)
                throw new InvalidDataException("PVF 解压长度无效");
            // 已知长度直接解压到最终数组，避免 MemoryStream 扩容和 ToArray 再复制。
            byte[] result = GC.AllocateUninitializedArray<byte>(size);
            int offset = 0;
            while (offset < result.Length)
            {
                int count = input.Read(result.AsSpan(offset));
                if (count == 0)
                    throw new InvalidDataException("PVF 解压长度不符");
                offset += count;
            }
            if (input.ReadByte() != -1)
                throw new InvalidDataException("PVF 解压数据超过声明长度");
            return result;
        }

        // 部分字符串池没有可靠的解压长度，保持流式读取及上限检查。
        using var output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                if (output.Length + count > MaxBytes)
                    throw new InvalidDataException("PVF 解压数据超过上限");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    internal static int I32(ReadOnlySpan<byte> bytes, int offset) => unchecked((int)U32(bytes, offset));
    internal static int Size(ReadOnlySpan<byte> bytes, int offset)
    {
        int value = I32(bytes, offset);
        return value is >= 0 and <= MaxBytes ? value : throw new InvalidDataException("PVF 声明大小无效");
    }
}
