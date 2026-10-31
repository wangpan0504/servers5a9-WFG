// 移植自 S5A9GmTool（rewio，MIT License，见仓库根目录 THIRD-PARTY.md），已改命名空间与可见性。
#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DfoServer.Network.Management.ImagePack
{
    // IMG v2：0x0E/0x0F/0x10 为像素帧，0x11 为链接帧。v4/v6 转交 ImgFrameReader（本仓库自写）。
    internal static class NpkImageDecoder
    {
        private const int TypeArgb1555 = 0x0E;
        private const int TypeArgb4444 = 0x0F;
        private const int TypeArgb8888 = 0x10;
        private const int TypeLink = 0x11;
        private const int MaxCanvas = 256;

        public static bool TryDecodeFrame(byte[] blob, int frameIndex, out int width, out int height, out byte[] rgba)
        {
            width = 0;
            height = 0;
            rgba = null;
            if (blob == null || blob.Length < 32 || frameIndex < 0)
                return false;
            if (!Encoding.ASCII.GetString(blob, 0, 15).StartsWith("Neople Img File", StringComparison.Ordinal))
                return false;

            var indexLength = BitConverter.ToInt32(blob, 16);
            var version = BitConverter.ToInt32(blob, 24);
            var frameCount = BitConverter.ToInt32(blob, 28);
            // v4/v6 调色板格式由本仓库自写的 ImgFrameReader 解码，再按同样规则放到画布上。
            if (version == 4 || version == 6)
                return TryDecodePaletted(blob, frameIndex, out width, out height, out rgba);
            // #959：先按预算与帧表容量校验，再分配帧数组。
            if (version != 2 || !ImgBudget.FrameTableFits(frameCount, indexLength) || frameIndex >= frameCount)
                return false;
            if (32L + indexLength > blob.Length)
                return false;

            var frames = new FrameInfo[frameCount];
            var pos = 32;
            var pixelCursor = 32L + indexLength;
            for (var i = 0; i < frameCount; i++)
            {
                if (pos + 4 > blob.Length)
                    return false;
                var type = BitConverter.ToInt32(blob, pos);
                if (type == TypeLink)
                {
                    if (pos + 8 > blob.Length)
                        return false;
                    frames[i] = FrameInfo.Link(BitConverter.ToInt32(blob, pos + 4));
                    pos += 8;
                    continue;
                }

                if (pos + 36 > blob.Length)
                    return false;
                var compressed = BitConverter.ToInt32(blob, pos + 4);
                var frameWidth = BitConverter.ToInt32(blob, pos + 8);
                var frameHeight = BitConverter.ToInt32(blob, pos + 12);
                var size = BitConverter.ToInt32(blob, pos + 16);
                var keyX = BitConverter.ToInt32(blob, pos + 20);
                var keyY = BitConverter.ToInt32(blob, pos + 24);
                var maxWidth = BitConverter.ToInt32(blob, pos + 28);
                var maxHeight = BitConverter.ToInt32(blob, pos + 32);
                // 未压缩 v2 帧的声明 size 可能是 4，但 1×1 的 16 位像素实际只占 2 字节。
                // 与纸娃娃读取器一致：仅标记 6 使用声明的压缩长度。
                if (compressed != 6)
                {
                    var pixels = (long)frameWidth * frameHeight;
                    var bytesPerPixel = type == TypeArgb8888 ? 4 : 2;
                    if (frameWidth <= 0 || frameHeight <= 0 || pixels > int.MaxValue / bytesPerPixel)
                        return false;
                    size = (int)(pixels * bytesPerPixel);
                }
                if (size < 0 || pixelCursor + size > blob.Length)
                    return false;
                frames[i] = FrameInfo.Pixels(type, compressed, frameWidth, frameHeight, (int)pixelCursor, size, keyX, keyY, maxWidth, maxHeight);
                pos += 36;
                pixelCursor += size;
            }

            return TryMaterialize(blob, frames, frameIndex, out width, out height, out rgba);
        }

        private static bool TryMaterialize(
            byte[] blob,
            FrameInfo[] frames,
            int frameIndex,
            out int width,
            out int height,
            out byte[] rgba)
        {
            width = 0;
            height = 0;
            rgba = null;
            if (frameIndex < 0 || frameIndex >= frames.Length)
                return false;

            // #959：链接帧迭代跟随，最多 frames.Length 跳，长链不耗栈、环被拒绝。
            var frame = frames[frameIndex];
            for (var hops = 0; frame.IsLink; hops++)
            {
                if (hops >= frames.Length || frame.LinkIndex < 0 || frame.LinkIndex >= frames.Length)
                    return false;
                frame = frames[frame.LinkIndex];
            }

            if (frame.Width <= 0 || frame.Height <= 0 || frame.Size <= 0
                || frame.Width > ImgBudget.MaxSide || frame.Height > ImgBudget.MaxSide)
                return false;

            var pixels = DecodePixels(blob, frame);
            if (pixels == null)
                return false;

            var canvasWidth = frame.MaxWidth > 0 ? frame.MaxWidth : frame.Width;
            var canvasHeight = frame.MaxHeight > 0 ? frame.MaxHeight : frame.Height;
            if (canvasWidth <= 0 || canvasHeight <= 0 || canvasWidth > MaxCanvas || canvasHeight > MaxCanvas)
            {
                canvasWidth = frame.Width;
                canvasHeight = frame.Height;
            }

            if (frame.KeyX == 0 && frame.KeyY == 0 && canvasWidth == frame.Width && canvasHeight == frame.Height)
            {
                width = frame.Width;
                height = frame.Height;
                rgba = pixels;
                return true;
            }

            var canvas = new byte[canvasWidth * canvasHeight * 4];
            Blit(canvas, canvasWidth, canvasHeight, pixels, frame.Width, frame.Height, frame.KeyX, frame.KeyY);
            width = canvasWidth;
            height = canvasHeight;
            rgba = canvas;
            return true;
        }

        private static bool TryDecodePaletted(byte[] blob, int frameIndex, out int width, out int height, out byte[] rgba)
        {
            width = 0;
            height = 0;
            rgba = null;
            if (!ImgFrameReader.TryRead(blob, frameIndex, out var frame))
                return false;
            if (frame.CanvasWidth > MaxCanvas || frame.CanvasHeight > MaxCanvas
                || (frame.KeyX == 0 && frame.KeyY == 0 && frame.CanvasWidth == frame.Width && frame.CanvasHeight == frame.Height))
            {
                width = frame.Width;
                height = frame.Height;
                rgba = frame.Rgba;
                return true;
            }

            var canvas = new byte[frame.CanvasWidth * frame.CanvasHeight * 4];
            Blit(canvas, frame.CanvasWidth, frame.CanvasHeight, frame.Rgba, frame.Width, frame.Height, frame.KeyX, frame.KeyY);
            width = frame.CanvasWidth;
            height = frame.CanvasHeight;
            rgba = canvas;
            return true;
        }

        private static byte[] DecodePixels(byte[] blob, FrameInfo frame)
        {
            byte[] payload;
            if (frame.Compressed == 6)
            {
                // #959：只展开 w×h×每像素字节；zlib 损坏直接失败，不把压缩字节当像素。
                var needed = frame.Width * frame.Height * (frame.Type == TypeArgb8888 ? 4 : 2);
                payload = ImgBudget.Inflate(blob, frame.PixelOffset, frame.Size, needed);
                if (payload == null)
                    return null;
            }
            else
            {
                payload = new byte[frame.Size];
                Buffer.BlockCopy(blob, frame.PixelOffset, payload, 0, frame.Size);
            }

            switch (frame.Type)
            {
                case TypeArgb1555:
                    return payload.Length >= frame.Width * frame.Height * 2
                        ? FromArgb1555(payload, frame.Width, frame.Height)
                        : null;
                case TypeArgb4444:
                    return payload.Length >= frame.Width * frame.Height * 2
                        ? FromArgb4444(payload, frame.Width, frame.Height)
                        : null;
                case TypeArgb8888:
                    return payload.Length >= frame.Width * frame.Height * 4
                        ? FromArgb8888(payload, frame.Width, frame.Height)
                        : null;
                default:
                    return null;
            }
        }

        private static byte[] FromArgb1555(byte[] src, int width, int height)
        {
            var dest = new byte[width * height * 4];
            var di = 0;
            for (var i = 0; i < width * height; i++)
            {
                var pixel = (ushort)(src[i * 2] | (src[i * 2 + 1] << 8));
                dest[di++] = Scale5((pixel >> 10) & 0x1F);
                dest[di++] = Scale5((pixel >> 5) & 0x1F);
                dest[di++] = Scale5(pixel & 0x1F);
                dest[di++] = (pixel & 0x8000) != 0 ? (byte)255 : (byte)0;
            }
            return dest;
        }

        private static byte[] FromArgb4444(byte[] src, int width, int height)
        {
            var dest = new byte[width * height * 4];
            var di = 0;
            for (var i = 0; i < width * height; i++)
            {
                var pixel = (ushort)(src[i * 2] | (src[i * 2 + 1] << 8));
                dest[di++] = (byte)(((pixel >> 8) & 0xF) * 17);
                dest[di++] = (byte)(((pixel >> 4) & 0xF) * 17);
                dest[di++] = (byte)((pixel & 0xF) * 17);
                dest[di++] = (byte)(((pixel >> 12) & 0xF) * 17);
            }
            return dest;
        }

        private static byte[] FromArgb8888(byte[] src, int width, int height)
        {
            var dest = new byte[width * height * 4];
            var di = 0;
            for (var i = 0; i < width * height; i++)
            {
                var si = i * 4;
                dest[di++] = src[si + 2];
                dest[di++] = src[si + 1];
                dest[di++] = src[si];
                dest[di++] = src[si + 3];
            }
            return dest;
        }

        private static byte Scale5(int value)
        {
            return (byte)(value * 255 / 31);
        }

        internal static void Blit(byte[] dest, int destWidth, int destHeight, byte[] src, int srcWidth, int srcHeight, int x, int y)
        {
            for (var row = 0; row < srcHeight; row++)
            {
                var dy = y + row;
                if (dy < 0 || dy >= destHeight)
                    continue;
                for (var col = 0; col < srcWidth; col++)
                {
                    var dx = x + col;
                    if (dx < 0 || dx >= destWidth)
                        continue;
                    var si = (row * srcWidth + col) * 4;
                    var alpha = src[si + 3];
                    if (alpha == 0)
                        continue;
                    var di = (dy * destWidth + dx) * 4;
                    if (alpha == 255)
                    {
                        dest[di] = src[si];
                        dest[di + 1] = src[si + 1];
                        dest[di + 2] = src[si + 2];
                        dest[di + 3] = 255;
                        continue;
                    }

                    var destAlpha = dest[di + 3];
                    var outAlpha = alpha + destAlpha * (255 - alpha) / 255;
                    if (outAlpha == 0)
                        continue;
                    dest[di] = (byte)((src[si] * alpha + dest[di] * destAlpha * (255 - alpha) / 255) / outAlpha);
                    dest[di + 1] = (byte)((src[si + 1] * alpha + dest[di + 1] * destAlpha * (255 - alpha) / 255) / outAlpha);
                    dest[di + 2] = (byte)((src[si + 2] * alpha + dest[di + 2] * destAlpha * (255 - alpha) / 255) / outAlpha);
                    dest[di + 3] = (byte)outAlpha;
                }
            }
        }

        private readonly struct FrameInfo
        {
            public static FrameInfo Link(int linkIndex)
            {
                return new FrameInfo(true, linkIndex, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            }

            public static FrameInfo Pixels(
                int type,
                int compressed,
                int width,
                int height,
                int pixelOffset,
                int size,
                int keyX,
                int keyY,
                int maxWidth,
                int maxHeight)
            {
                return new FrameInfo(false, 0, type, compressed, width, height, pixelOffset, size, keyX, keyY, maxWidth, maxHeight);
            }

            private FrameInfo(
                bool isLink,
                int linkIndex,
                int type,
                int compressed,
                int width,
                int height,
                int pixelOffset,
                int size,
                int keyX,
                int keyY,
                int maxWidth,
                int maxHeight)
            {
                IsLink = isLink;
                LinkIndex = linkIndex;
                Type = type;
                Compressed = compressed;
                Width = width;
                Height = height;
                PixelOffset = pixelOffset;
                Size = size;
                KeyX = keyX;
                KeyY = keyY;
                MaxWidth = maxWidth;
                MaxHeight = maxHeight;
            }

            public bool IsLink { get; }
            public int LinkIndex { get; }
            public int Type { get; }
            public int Compressed { get; }
            public int Width { get; }
            public int Height { get; }
            public int PixelOffset { get; }
            public int Size { get; }
            public int KeyX { get; }
            public int KeyY { get; }
            public int MaxWidth { get; }
            public int MaxHeight { get; }
        }
    }
}
