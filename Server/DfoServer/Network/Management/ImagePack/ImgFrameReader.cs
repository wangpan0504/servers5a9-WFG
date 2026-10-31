using System.Buffers.Binary;
using System.Text;

namespace DfoServer.Network.Management.ImagePack;

// 一帧解码结果：Rgba 为 Width×Height 的 RGBA8888；KeyX/KeyY 是帧在 CanvasWidth×CanvasHeight 画布上的左上角。
internal sealed record ImgFrame(int Width, int Height, int KeyX, int KeyY, int CanvasWidth, int CanvasHeight, byte[] Rgba);

// 本仓库自写的 IMG 帧读取（不属于移植代码）：支持 v2 直接色与 v4/v6 调色板。
// 布局按本机客户端 ImagePacks2 样本核对（见 Docs/开发/GM.md“纸娃娃”一节）：
// 头 32 字节后，v4 = [颜色数][RGBA×n]；v6 = [调色板数][每个：颜色数 + RGBA×n]；其后是 indexLength 字节的帧表。
// v4/v6 的 0x0E 帧解压后长度恰为 w×h 时是 1 字节调色板下标；否则按 v2 的 16/32 位直接色解释（样本中 1×1 未压缩占位帧即此情形）。
// v5（含 DDS 纹理表）未实现，读取时返回 false。
internal static class ImgFrameReader
{
    const int TypeArgb1555 = 0x0E, TypeArgb4444 = 0x0F, TypeArgb8888 = 0x10, TypeLink = 0x11;
    const int MaxColors = 4096, MaxPalettes = 256, MaxSide = ImgBudget.MaxSide;

    readonly record struct Entry(bool Link, int Target, int Type, int Compression, int Width, int Height, int Offset, int Size, int KeyX, int KeyY, int MaxWidth, int MaxHeight);

    public static int Version(byte[] blob) =>
        blob is { Length: >= 32 } && Encoding.ASCII.GetString(blob, 0, 15) == "Neople Img File" ? BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(24)) : -1;

    public static int PaletteCount(byte[] blob) => Version(blob) switch
    {
        4 => 1,
        6 => blob.Length >= 36 ? BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(32)) : 0,
        _ => 0
    };

    public static bool TryRead(byte[] blob, int frameIndex, out ImgFrame? frame, int palette = 0)
    {
        frame = null;
        try { return TryReadCore(blob, frameIndex, palette, out frame); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException) { return false; }
    }

    static bool TryReadCore(byte[] blob, int frameIndex, int palette, out ImgFrame? frame)
    {
        frame = null;
        int version = Version(blob);
        if (version is not (2 or 4 or 6) || frameIndex < 0) return false;
        var span = blob.AsSpan();
        int indexLength = I32(span, 16), count = I32(span, 28);
        if (!ImgBudget.FrameTableFits(count, indexLength) || frameIndex >= count) return false;
        int pos = 32;
        byte[][] palettes = [];
        if (version == 4) palettes = [ReadPalette(span, ref pos)];
        else if (version == 6)
        {
            int groups = I32(span, pos); pos += 4;
            if (groups is < 0 or > MaxPalettes) return false;
            palettes = new byte[groups][];
            for (int i = 0; i < groups; i++) palettes[i] = ReadPalette(span, ref pos);
        }
        if ((long)pos + indexLength > blob.Length) return false;
        var entries = new Entry[count];
        int end = pos + indexLength;
        long data = end;
        for (int i = 0; i < count; i++)
        {
            if (pos + 8 > end) return false;
            int type = I32(span, pos);
            if (type == TypeLink) { entries[i] = new(true, I32(span, pos + 4), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0); pos += 8; continue; }
            if (pos + 36 > end) return false;
            // 未压缩帧实际占 w×h×每像素字节（0x10 为 4，其余 2），头里的 size 不可信：
            // 样本 gn_coat0000c.img（v2）的 165 个 1×1 未压缩帧都声明 4 字节，按 2 字节累计才与文件长度一致。
            int size = I32(span, pos + 16);
            if (I32(span, pos + 4) != 6)
                size = checked(I32(span, pos + 8) * I32(span, pos + 12) * (type == TypeArgb8888 ? 4 : 2));
            if (size < 0 || data + size > blob.Length) return false;
            entries[i] = new(false, 0, type, I32(span, pos + 4), I32(span, pos + 8), I32(span, pos + 12), (int)data, size,
                I32(span, pos + 20), I32(span, pos + 24), I32(span, pos + 28), I32(span, pos + 32));
            pos += 36;
            data += size;
        }
        // 链接帧最多跟随 count 次，防止环。
        var entry = entries[frameIndex];
        for (int hops = 0; entry.Link; hops++)
        {
            if (hops >= count || entry.Target < 0 || entry.Target >= count) return false;
            entry = entries[entry.Target];
        }
        if (entry.Width is <= 0 or > MaxSide || entry.Height is <= 0 or > MaxSide || entry.Size <= 0) return false;
        int pixels = entry.Width * entry.Height;
        // #959：压缩帧最多展开 w×h×4 字节（调色板/16 位/32 位都不超过），zlib 损坏直接失败。
        byte[]? payload = entry.Compression == 6
            ? ImgBudget.Inflate(blob, entry.Offset, entry.Size, pixels * 4)
            : blob.AsSpan(entry.Offset, entry.Size).ToArray();
        if (payload is null) return false;
        byte[]? rgba;
        if (version != 2 && entry.Type == TypeArgb1555 && payload.Length == pixels && palettes.Length > 0)
            rgba = FromPalette(payload, palettes[palette >= 0 && palette < palettes.Length ? palette : 0]);
        else rgba = Direct(entry.Type, payload, pixels);
        if (rgba is null) return false;
        frame = new(entry.Width, entry.Height, entry.KeyX, entry.KeyY,
            entry.MaxWidth > 0 ? entry.MaxWidth : entry.Width, entry.MaxHeight > 0 ? entry.MaxHeight : entry.Height, rgba);
        return true;
    }

    static int I32(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadInt32LittleEndian(span.Slice(at, 4));

    // 调色板颜色按 R、G、B、A 字节顺序存放（样本 sm_body0000.img 第 90 帧以此顺序还原为正常肤色）。
    static byte[] ReadPalette(ReadOnlySpan<byte> span, ref int pos)
    {
        int colors = I32(span, pos); pos += 4;
        if (colors is < 0 or > MaxColors) throw new InvalidDataException("IMG 调色板颜色数无效");
        byte[] table = span.Slice(pos, colors * 4).ToArray();
        pos += colors * 4;
        return table;
    }

    static byte[] FromPalette(byte[] indexes, byte[] palette)
    {
        var rgba = new byte[indexes.Length * 4];
        for (int i = 0; i < indexes.Length; i++)
        {
            int at = indexes[i] * 4;
            if (at + 4 > palette.Length) continue; // 越界下标按透明处理
            palette.AsSpan(at, 4).CopyTo(rgba.AsSpan(i * 4));
        }
        return rgba;
    }

    static byte[]? Direct(int type, byte[] src, int pixels)
    {
        switch (type)
        {
            case TypeArgb1555 when src.Length >= pixels * 2:
            {
                var dest = new byte[pixels * 4];
                for (int i = 0; i < pixels; i++)
                {
                    int value = src[i * 2] | src[i * 2 + 1] << 8;
                    dest[i * 4] = (byte)(((value >> 10) & 31) * 255 / 31);
                    dest[i * 4 + 1] = (byte)(((value >> 5) & 31) * 255 / 31);
                    dest[i * 4 + 2] = (byte)((value & 31) * 255 / 31);
                    dest[i * 4 + 3] = (value & 0x8000) != 0 ? (byte)255 : (byte)0;
                }
                return dest;
            }
            case TypeArgb4444 when src.Length >= pixels * 2:
            {
                var dest = new byte[pixels * 4];
                for (int i = 0; i < pixels; i++)
                {
                    int value = src[i * 2] | src[i * 2 + 1] << 8;
                    dest[i * 4] = (byte)(((value >> 8) & 15) * 17);
                    dest[i * 4 + 1] = (byte)(((value >> 4) & 15) * 17);
                    dest[i * 4 + 2] = (byte)((value & 15) * 17);
                    dest[i * 4 + 3] = (byte)(((value >> 12) & 15) * 17);
                }
                return dest;
            }
            case TypeArgb8888 when src.Length >= pixels * 4:
            {
                var dest = new byte[pixels * 4];
                for (int i = 0; i < pixels; i++)
                {
                    dest[i * 4] = src[i * 4 + 2];
                    dest[i * 4 + 1] = src[i * 4 + 1];
                    dest[i * 4 + 2] = src[i * 4];
                    dest[i * 4 + 3] = src[i * 4 + 3];
                }
                return dest;
            }
            default:
                return null;
        }
    }

}
