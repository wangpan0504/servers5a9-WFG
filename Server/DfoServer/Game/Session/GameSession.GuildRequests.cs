using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    readonly Guid guildRequestNonce = Guid.NewGuid();
    public Action? GuildManagementClosing { get; set; }
    public ulong GuildRequestHighWatermark { get; private set; }

    public void ObserveGuildRequest(ulong serial) => GuildRequestHighWatermark = Math.Max(GuildRequestHighWatermark, serial);

    public string GuildRequestIdentity(ulong serial)
    {
        Span<byte> data = stackalloc byte[24];
        guildRequestNonce.TryWriteBytes(data);
        BinaryPrimitives.WriteUInt64LittleEndian(data[16..], serial);
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }
}
