using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Settings;

namespace DfoServer.Network.Parsers;

internal static class SettingsRequests
{
    public static byte SeenNotice(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4 || BinaryPrimitives.ReadUInt32LittleEndian(body) >= 128)
            throw new InvalidDataException("已看提示请求长度或编号无效");
        return body[0];
    }
    public static (bool Add, int NpcId) NpcBookmark(ReadOnlySpan<byte> body)
    {
        if (body.Length != 5 || body[0] > 1 || BinaryPrimitives.ReadInt32LittleEndian(body[1..]) <= 0)
            throw new InvalidDataException("人物辞典收藏请求无效");
        return (body[0] == 1, BinaryPrimitives.ReadInt32LittleEndian(body[1..]));
    }
    public static ImmutableArray<byte> QuestNotify(ReadOnlySpan<byte> body)
    {
        // 02280B00：count:u8及至多四个qid:u32；选角恢复则固定读取四个u32。
        if (body.IsEmpty || body[0] > QuestNotifyRules.Slots || body.Length != 1 + body[0] * sizeof(uint))
            throw new InvalidDataException("任务提示保存数量或正文长度无效");
        byte[] packed = new byte[QuestNotifyRules.Length];
        body[1..].CopyTo(packed);
        for (int slot = 0; slot < body[0]; slot++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(slot * sizeof(uint))) == 0)
                throw new InvalidDataException("任务提示保存不能包含空编号");
        var entries = ImmutableArray.Create(packed);
        QuestNotifyRules.Validate(entries);
        return entries;
    }

    public static ImmutableArray<byte> RecentEmoticons(ReadOnlySpan<byte> body)
    {
        // 02602440整块发送85B，含13B不透明保留区；通知1384不带该前缀。
        if (body.Length != 13 + ClientSettingsService.RecentEmoticonLength)
            throw new InvalidDataException("最近表情请求必须是完整85字节");
        var entries = ImmutableArray.Create(body[13..].ToArray());
        ClientSettingsService.ValidateRecentEmoticons(entries);
        return entries;
    }

    public static ImmutableArray<ChatEmoticonMacro> ChatEmoticons(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2) throw new InvalidDataException("聊天表情请求缺少数量");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body);
        if (count > 256) throw new InvalidDataException("聊天表情请求数量越界");
        var rows = ImmutableArray.CreateBuilder<ChatEmoticonMacro>(count);
        var seen = new HashSet<ushort>();
        int offset = 2;
        for (int i = 0; i < count; i++)
        {
            if (body.Length - offset < 6) throw new InvalidDataException("聊天表情请求不完整");
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 2)..]);
            offset += 6;
            if (id == 0 || id > 32767 || !seen.Add(id) || size > 259 || size > body.Length - offset)
                throw new InvalidDataException("聊天表情编号或文本长度无效");
            var text = body.Slice(offset, (int)size);
            if (text.Contains((byte)0)) throw new InvalidDataException("聊天表情文本包含零字节");
            rows.Add(new(id, ImmutableArray.Create(text.ToArray())));
            offset += (int)size;
        }
        if (offset != body.Length) throw new InvalidDataException("聊天表情请求包含多余数据");
        return rows.MoveToImmutable();
    }

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
