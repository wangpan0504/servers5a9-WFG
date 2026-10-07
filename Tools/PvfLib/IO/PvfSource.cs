using Microsoft.Win32.SafeHandles;

namespace PvfLib.IO;

// 文件模式按偏移读取，不把整份压缩归档复制进托管堆；内存模式持有独立副本。
internal sealed class PvfSource : IDisposable
{
    private readonly SafeFileHandle? handle;
    private byte[]? bytes;
    private bool disposed;
    public long Length { get; }

    public PvfSource(string path)
    {
        handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            Length = RandomAccess.GetLength(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public PvfSource(ReadOnlySpan<byte> data)
    {
        bytes = data.ToArray();
        Length = bytes.Length;
    }

    public byte[] Read(int offset, int length)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (offset < 0 || length < 0 || offset + (long)length > Length)
            throw new InvalidDataException("PVF 读取范围越界");
        if (bytes is not null)
            return bytes.AsSpan(offset, length).ToArray();
        byte[] result = GC.AllocateUninitializedArray<byte>(length);
        int read = 0;
        while (read < length)
        {
            int count = RandomAccess.Read(handle!, result.AsSpan(read), offset + (long)read);
            if (count == 0)
                throw new EndOfStreamException("PVF 文件被截断");
            read += count;
        }
        return result;
    }

    public string ComputeSha256()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        const int block = 64 * 1024;
        for (long offset = 0; offset < Length; offset += block)
            hash.AppendData(Read(checked((int)offset), (int)Math.Min(block, Length - offset)));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        handle?.Dispose();
        bytes = null;
    }
}
