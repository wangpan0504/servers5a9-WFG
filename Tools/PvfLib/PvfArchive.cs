using PvfLib.Caching;
using PvfLib.Format;
using PvfLib.IO;
using PvfLib.Scripting;
using static PvfLib.Format.PvfCodec;

namespace PvfLib;

// 只读归档负责索引、资源所有权和读取调度，玩法含义由调用方的 Catalog 解释。
public sealed class PvfArchive : IDisposable
{
    private readonly PvfSource source;
    private readonly bool protectedFormat;
    private readonly int bodyOffset;
    private readonly (int End, int Size)[] groups;
    private readonly Dictionary<PvfPathKey, Entry> entries;
    private PvfStringPool? strings;
    private readonly ByteBudgetCache<int, byte[]> chunks;
    private readonly ByteBudgetCache<string, string> texts;
    private bool disposed;
    private readonly record struct Entry(int DataType, int Size, int Group, int Offset);

    public string Format => protectedFormat ? "protected_nkpi" : "nkpi";
    public int FileCount => entries.Count;
    public int GroupCount => groups.Length;
    public int CachedChunks => chunks.Count;
    public long CachedChunkBytes => chunks.Bytes;
    public long CachedTextBytes => texts.Bytes;
    public IEnumerable<PvfFile> Files
    {
        get
        {
            EnsureOpen();
            foreach (var entry in entries)
            {
                yield return new(entry.Key.Path, entry.Value.DataType, entry.Value.Size);
                EnsureOpen();
            }
        }
    }

    public static PvfArchive Open(string path) => Open(path, new());
    public static PvfArchive Open(string path, PvfReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return Create(new PvfSource(path), options);
    }

    public static PvfArchive OpenBytes(ReadOnlySpan<byte> bytes) => OpenBytes(bytes, new());
    public static PvfArchive OpenBytes(ReadOnlySpan<byte> bytes, PvfReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (bytes.Length < 48 || bytes.Length > MaxBytes)
            throw new InvalidDataException("PVF 长度无效");
        return Create(new PvfSource(bytes), options);
    }

    private static PvfArchive Create(PvfSource source, PvfReadOptions options)
    {
        try
        {
            return new(source, options);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private PvfArchive(PvfSource source, PvfReadOptions options)
    {
        this.source = source;
        if (source.Length < 48 || source.Length > MaxBytes)
            throw new InvalidDataException("PVF 文件长度无效或超过 512 MiB");
        chunks = new(options.ChunkCacheBytes, value => value.LongLength);
        texts = new(options.TextCacheBytes, value => 24L + value.Length * 2L);
        byte[] header = source.Read(0, 48);
        for (int i = 24; i < 28; i++)
            header[i] ^= 0x55;
        Crypt(header, "HeaD", false);
        if (U32(header, 0) != 0x69706B6E)
        {
            header = source.Read(0, 48);
            Crypt(header, "hEAd", true);
            if (U32(header, 0) != 0x69706B6E)
                throw new InvalidDataException("不支持的 PVF 格式");
            protectedFormat = true;
        }

        int fileCount = Size(header, 24), bodySize = Size(header, 32), groupCount = Size(header, 36);
        int hashSize = Size(header, 40), nameSize = Size(header, 44);
        long nameOffset = 48L + fileCount * 24L + hashSize;
        long groupOffset = nameOffset + nameSize;
        long body = groupOffset + groupCount * 8L;
        if (body + bodySize > source.Length)
            throw new InvalidDataException("PVF 表范围越界");
        bodyOffset = (int)body;
        // 仅目录和字符串池在打开时读取；压缩内容保留在文件中按需访问。
        byte[] metadata = source.Read(48, bodyOffset - 48);
        strings = new(metadata.AsSpan((int)nameOffset - 48, nameSize), protectedFormat);
        var groupBytes = metadata.AsSpan((int)groupOffset - 48);
        Crypt(groupBytes, protectedFormat ? "grpi" : "GRPI", protectedFormat);
        groups = new (int, int)[groupCount];
        int previous = 0;
        for (int i = 0; i < groupCount; i++)
        {
            int end = Size(groupBytes, i * 8), size = Size(groupBytes, i * 8 + 4);
            if (end <= previous || end > bodySize)
                throw new InvalidDataException("PVF 分块压缩范围无效");
            groups[i] = (end, size);
            previous = end;
        }

        entries = new(fileCount, new PvfPathKey.Comparer());
        var directories = new Dictionary<string, string>(StringComparer.Ordinal);
        var rawDirectories = new Dictionary<int, string>();
        for (int i = 0; i < fileCount; i++)
        {
            int off = i * 24;
            int directory = I32(metadata, off + 4);
            if (!rawDirectories.TryGetValue(directory, out string? dir))
                rawDirectories.Add(directory, dir = strings.Resolve(directory));
            string name = strings.Resolve(I32(metadata, off));
            string path = NormalizePath(dir + "/" + name);
            int group = Size(metadata, off + 8), start = Size(metadata, off + 12), size = Size(metadata, off + 16), type = Size(metadata, off + 20);
            if (path.Length == 0 || group >= groups.Length || (long)start + size > groups[group].Size)
                throw new InvalidDataException($"PVF 文件范围无效: {path}");
            // 规范化之后共享目录，临时目录表在构建完成后释放；条目仍按首项顺序保存。
            int slash = path.LastIndexOf('/');
            var parent = slash < 0 ? ReadOnlySpan<char>.Empty : path.AsSpan(0, slash);
            if (!directories.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(parent, out string? canonicalDirectory))
                directories.Add(canonicalDirectory = parent.ToString(), canonicalDirectory);
            string fileName = path[(slash + 1)..];
            entries.TryAdd(new(canonicalDirectory, fileName), new(type, size, group, start));
        }
    }

    public static string NormalizePath(string path) => PvfPath.Normalize(path);

    public bool Contains(string path)
    {
        EnsureOpen();
        return TryFindEntry(NormalizePath(path), out _);
    }

    public PvfFile? FindFile(string path)
    {
        EnsureOpen();
        string normalized = NormalizePath(path);
        return TryFindEntry(normalized, out var entry) ? new(normalized, entry.DataType, entry.Size) : null;
    }

    private Entry FindEntry(string path)
    {
        EnsureOpen();
        return TryFindEntry(NormalizePath(path), out var entry) ? entry : throw new FileNotFoundException("PVF 内文件不存在", path);
    }

    private bool TryFindEntry(string normalized, out Entry entry)
        => entries.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(normalized, out entry);

    private ReadOnlySpan<byte> ReadEntry(Entry entry)
    {
        byte[] chunk = chunks.GetOrAdd(entry.Group, ReadChunk);
        return chunk.AsSpan(entry.Offset, entry.Size);
    }

    public byte[] ReadRaw(string path) => ReadEntry(FindEntry(path)).ToArray();

    // 分块遍历不填充常规读取缓存；回调得到独立字节副本，读取异常由调用方处理。
    public void ScanRawFiles(IEnumerable<PvfFile> files, Action<PvfFile, Func<byte[]>> inspect)
    {
        EnsureOpen();
        foreach (var group in files.GroupBy(file => FindEntry(file.Path).Group))
        {
            var chunk = new Lazy<byte[]>(() => chunks.TryGet(group.Key, out var cached) ? cached : ReadChunk(group.Key));
            foreach (var file in group)
            {
                var entry = FindEntry(file.Path);
                inspect(file, () =>
                {
                    EnsureOpen();
                    return chunk.Value.AsSpan(entry.Offset, entry.Size).ToArray();
                });
            }
        }
    }

    public PvfToken[] ReadTokens(string path)
    {
        EnsureOpen();
        if (!TryFindEntry(NormalizePath(path), out var entry) || entry.DataType != 1)
            throw new InvalidDataException("PVF 文件不是 token 脚本: " + path);
        return PvfTokenDecoder.Tokens(ReadEntry(entry));
    }

    public string ReadText(string path)
    {
        EnsureOpen();
        return texts.GetOrAdd(NormalizePath(path), DecodeText);
    }

    private string DecodeText(string path)
    {
        var entry = FindEntry(path);
        if (entry.DataType is not (1 or 3))
            throw new InvalidDataException("PVF 文件不支持文本解码: " + path);
        return PvfTokenDecoder.Text(ReadEntry(entry), entry.DataType, ResolveString);
    }

    public string ResolveString(int value)
    {
        EnsureOpen();
        return strings!.Resolve(value);
    }

    private byte[] ReadChunk(int index)
    {
        EnsureOpen();
        int previous = index == 0 ? 0 : groups[index - 1].End;
        byte[] encrypted = source.Read(bodyOffset + previous, groups[index].End - previous);
        Crypt(encrypted, protectedFormat ? "bODy" : "BodY", protectedFormat);
        return Inflate(encrypted, groups[index].Size);
    }

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        source.Dispose();
        chunks.Clear();
        texts.Clear();
        entries.Clear();
        entries.TrimExcess();
        strings = null;
    }
}
