using System.IO.Compression;

namespace DfoServer.Network.Management.ImagePack;

// IMG/NPK 解码预算（#959）。依据 2026-10-31 本机客户端 ImagePacks2 只读统计
// （ImagePacks2：7649 个 NPK、151527 个 IMG、15000966 帧、0 解析错误）：
// 单 IMG 最大 69,120,277 字节（sprite_interface.NPK）、单 IMG 最大帧数 3293（sprite_item_stackable.NPK）、
// 最大边 3584、单帧最大 RGBA/解压 10,243,200 字节。各预算均留至少 4 倍余量；超出视为损坏输入并拒绝。
internal static class ImgBudget
{
    public const int MaxImgBytes = 256 * 1024 * 1024;
    public const int MaxFrames = 16_384;
    public const int MaxSide = 4096;
    const int MinIndexEntryBytes = 8; // 链接帧表项 8 字节，像素帧 36 字节

    // 分配帧数组前先确认帧数在预算内，且帧表至少能容纳 frameCount 个最短表项。
    public static bool FrameTableFits(int frameCount, int indexLength) =>
        frameCount > 0 && frameCount <= MaxFrames && indexLength > 0 && frameCount <= indexLength / MinIndexEntryBytes;

    // 只展开解码会读取的前 limit 字节，避免压缩数据无上限膨胀；zlib 损坏返回 null（不再回退为原始字节当直接色）。
    public static byte[]? Inflate(byte[] blob, int offset, int count, int limit)
    {
        if (limit <= 0 || offset < 0 || count <= 0 || (long)offset + count > blob.Length) return null;
        try
        {
            using var input = new MemoryStream(blob, offset, count, false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(Math.Min(limit, 1 << 16));
            var chunk = new byte[Math.Min(limit, 1 << 16)];
            while (output.Length < limit)
            {
                int n = zlib.Read(chunk, 0, (int)Math.Min(chunk.Length, limit - output.Length));
                if (n <= 0) break;
                output.Write(chunk, 0, n);
            }
            return output.ToArray();
        }
        catch (InvalidDataException) { return null; }
    }
}
