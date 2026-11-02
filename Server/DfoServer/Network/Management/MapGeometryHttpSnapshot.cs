using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DfoServer.Network.Management;

// 仅保存已装配几何签名的只读副本，不重新读取PVF，也不持有角色或owner。
internal sealed class MapGeometryHttpSnapshot
{
    internal const int MaxPathCharacters = 480;
    internal const int MaxPathBytes = MaxPathCharacters * 4;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    readonly Dictionary<string, byte[]> signatures = new(StringComparer.Ordinal);
    readonly byte[] version;

    public MapGeometryHttpSnapshot(IEnumerable<KeyValuePair<string, byte[]>> source)
    {
        foreach (var row in source)
        {
            if (!ValidPath(row.Key) || row.Value is not { Length: 32 } ||
                !signatures.TryAdd(row.Key, row.Value.ToArray()))
                throw new InvalidDataException("Invalid map geometry signature snapshot");
        }
        // 全库沿已有资源目录规模；32768是单依赖图/客户端缓存预算，不限制全库。
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("DGV1"u8);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(number, signatures.Count); hash.AppendData(number);
        foreach (var row in signatures.OrderBy(row => row.Key, StringComparer.Ordinal))
        {
            byte[] path = Utf8.GetBytes(row.Key);
            BinaryPrimitives.WriteUInt16LittleEndian(number, checked((ushort)path.Length));
            hash.AppendData(number[..2]); hash.AppendData(path); hash.AppendData(row.Value);
        }
        version = hash.GetHashAndReset();
    }

    public byte[] Version => version.ToArray();
    public byte[]? Signature(string path) => signatures.TryGetValue(path, out var value) ? value.ToArray() : null;
    public bool HasVersion(ReadOnlySpan<byte> expected) => expected.SequenceEqual(version);

    internal static bool ValidPath(string? path)
    {
        if (path is null || path.Length > MaxPathCharacters || !path.StartsWith("map/", StringComparison.Ordinal) ||
            path.Contains("..", StringComparison.Ordinal) || path.IndexOfAny(['\\', ':', '\0']) >= 0 ||
            path.Any(char.IsControl) || path.Contains("//", StringComparison.Ordinal) || path.Contains("/./", StringComparison.Ordinal) ||
            path.EndsWith('/')) return false;
        try { return Utf8.GetByteCount(path) <= MaxPathBytes; }
        catch (EncoderFallbackException) { return false; }
    }
}
