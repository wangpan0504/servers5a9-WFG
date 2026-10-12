using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Settings;

namespace DfoServer.Network.Parsers;

internal static class SettingsRequests
{
    public static ImmutableArray<byte> GameOptions(ReadOnlySpan<byte> body) => Blob(body, ClientSettingsService.GameOptionLength);
    public static ImmutableArray<byte> CharacterOptions(ReadOnlySpan<byte> body) => Blob(body, ClientSettingsService.CharacterOptionLength);
    public static ImmutableArray<byte> Hotkeys(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4 || body.Length > HotkeySettingsRules.MaximumTextLength + 4 ||
            BinaryPrimitives.ReadUInt32LittleEndian(body) != body.Length - 4)
            throw new InvalidDataException("键位数据长度不匹配");
        var text = ImmutableArray.Create(body[4..].ToArray());
        HotkeySettingsRules.Validate(text);
        return text;
    }

    static ImmutableArray<byte> Blob(ReadOnlySpan<byte> body, int length)
    {
        if (body.Length != length + 4 || BinaryPrimitives.ReadUInt32LittleEndian(body) != length)
            throw new InvalidDataException("设置数据长度不匹配");
        return ImmutableArray.Create(body[4..].ToArray());
    }
}
