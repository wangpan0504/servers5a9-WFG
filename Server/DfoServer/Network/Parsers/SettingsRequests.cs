using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Settings;

namespace DfoServer.Network.Parsers;

internal static class SettingsRequests
{
    public static ImmutableArray<byte> GameOptions(ReadOnlySpan<byte> body) => Blob(body, ClientSettingsService.GameOptionLength);
    public static ImmutableArray<byte> CharacterOptions(ReadOnlySpan<byte> body) => Blob(body, ClientSettingsService.CharacterOptionLength);
    public static (byte Bank, ImmutableArray<byte> Text) QuickChat(ReadOnlySpan<byte> body)
    {
        // 匹配客户端368实际请求：bank:u8 + length:u32=300 + 300B原文；两个银行分别提交。
        if (body.Length != ClientSettingsService.QuickChatLength + 5 || body[0] > 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[1..]) != ClientSettingsService.QuickChatLength)
            throw new InvalidDataException("快捷聊天银行数据长度不匹配");
        return (body[0], ImmutableArray.Create(body[5..].ToArray()));
    }

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
