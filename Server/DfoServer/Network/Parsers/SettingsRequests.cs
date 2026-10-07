using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Settings;

namespace DfoServer.Network.Parsers;

internal static class SettingsRequests
{
    public static ImmutableArray<byte> GameOptions(ReadOnlySpan<byte> body) => Blob(body, ClientSettingsService.GameOptionLength);
    public static ImmutableArray<byte> CharacterOptions(ReadOnlySpan<byte> body) => Blob(body, ClientSettingsService.CharacterOptionLength);

    static ImmutableArray<byte> Blob(ReadOnlySpan<byte> body, int length)
    {
        if (body.Length != length + 4 || BinaryPrimitives.ReadUInt32LittleEndian(body) != length)
            throw new InvalidDataException("设置数据长度不匹配");
        return ImmutableArray.Create(body[4..].ToArray());
    }
}
