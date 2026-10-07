namespace PvfLib;

public sealed record PvfReadOptions
{
    public long ChunkCacheBytes { get; init; } = 64L * 1024 * 1024;
    public long TextCacheBytes { get; init; } = 16L * 1024 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ChunkCacheBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(TextCacheBytes);
    }
}
