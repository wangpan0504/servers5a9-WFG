using System.Buffers.Binary;
using System.Collections.Immutable;

namespace DfoServer.Network.Builders;

internal static class SettingsPackets
{
    public static byte[] SeenNotices(ImmutableArray<byte> ids)
    {
        if (ids.IsDefaultOrEmpty) return [0];
        if (ids.Length > 128 || ids.Any(id => id >= 128) || ids.Distinct().Count() != ids.Length)
            throw new InvalidDataException("已看提示列表编号无效或重复");
        return [(byte)ids.Length, .. ids];
    }
    public static byte[] NpcBookmarks(ImmutableArray<byte> entries)
    {
        if (entries.IsDefault) return new byte[4];
        DfoServer.Game.Settings.ClientSettingsService.ValidateNpcBookmarks(entries);
        byte[] body = new byte[4 + entries.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body, entries.Length / 4);
        entries.AsSpan().CopyTo(body.AsSpan(4));
        return body;
    }
    public static byte[] RecentEmoticons(ImmutableArray<byte> entries)
    {
        if (entries.IsDefault) return new byte[DfoServer.Game.Settings.ClientSettingsService.RecentEmoticonLength];
        DfoServer.Game.Settings.ClientSettingsService.ValidateRecentEmoticons(entries);
        return entries.ToArray();
    }

    public static byte[] ChatEmoticons(ImmutableArray<DfoServer.GameWorld.Characters.ChatEmoticonEntry> eligible,
        ImmutableArray<DfoServer.Game.Settings.ChatEmoticonMacro> saved)
    {
        var macros = saved.IsDefault ? new Dictionary<ushort, ImmutableArray<byte>>() : saved.ToDictionary(row => row.Id, row => row.Text);
        int length = 2 + eligible.Sum(row => 8 + (macros.TryGetValue(row.Id, out var text) ? text.Length : 0));
        byte[] body = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(body, checked((ushort)eligible.Length));
        int offset = 2;
        for (int i = 0; i < eligible.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(offset), checked((ushort)i));
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(offset + 2), eligible[i].Id);
            var text = macros.GetValueOrDefault(eligible[i].Id);
            int size = text.IsDefault ? 0 : text.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset + 4), checked((uint)size));
            if (size != 0) text.AsSpan().CopyTo(body.AsSpan(offset + 8));
            offset += 8 + size;
        }
        return body;
    }

    public static byte[] Emotion(ushort value, bool success = true)
    {
        byte[] body = new byte[3];
        body[0] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), value);
        return body;
    }

    public static byte[] HotkeysInitialization(byte profile)
    {
        if (profile > 1) throw new InvalidDataException("键位初始化配置类型无效");
        // 原生 NONE 哨兵按当前 bank 加载资源默认，并由客户端回送完整198；不作为存档文本接受。
        return [profile, 4, 0, 0, 0, (byte)'N', (byte)'O', (byte)'N', (byte)'E'];
    }

    public static byte[] Hotkeys(byte profile, ImmutableArray<byte> text)
    {
        if (profile > 1) throw new InvalidDataException("键位回放配置类型无效");
        DfoServer.Game.Settings.HotkeySettingsRules.Validate(text);
        byte[] body = new byte[text.Length + 5];
        body[0] = profile;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), checked((uint)text.Length));
        text.AsSpan().CopyTo(body.AsSpan(5));
        return body;
    }

    public static byte[] Visibility(ushort id, byte flags)
    {
        byte[] body = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(body, id);
        body[2] = flags;
        return body;
    }

    public static byte[] GameOptions(ImmutableArray<byte> options, ImmutableArray<byte> quickChat0 = default,
        ImmutableArray<byte> quickChat1 = default)
    {
        if (options.IsDefault || options.Length != 204)
            throw new InvalidDataException("主设置回放长度不匹配");
        return Compose(options.AsSpan(), quickChat0, quickChat1);
    }

    public static byte[] GameOptionsInitialization(ImmutableArray<byte> quickChat0 = default,
        ImmutableArray<byte> quickChat1 = default)
    {
        // 匹配客户端将全部 7FFF 识别为未设置，采用原生默认并完成模型初始化。
        byte[] options = new byte[204];
        for (int offset = 0; offset < options.Length; offset += 2)
            BinaryPrimitives.WriteUInt16LittleEndian(options.AsSpan(offset), 0x7FFF);
        return Compose(options, quickChat0, quickChat1);
    }

    // 账号通知固定为：主设置u32长度+原文，随后两个带u32长度的快捷聊天银行；缺省银行写0长度。
    static byte[] Compose(ReadOnlySpan<byte> options, ImmutableArray<byte> quickChat0, ImmutableArray<byte> quickChat1)
    {
        int bank0 = BankLength(quickChat0), bank1 = BankLength(quickChat1);
        byte[] body = new byte[checked(4 + options.Length + 4 + bank0 + 4 + bank1)];
        BinaryPrimitives.WriteUInt32LittleEndian(body, checked((uint)options.Length));
        options.CopyTo(body.AsSpan(4));
        int offset = 4 + options.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), checked((uint)bank0));
        if (bank0 > 0) quickChat0.AsSpan().CopyTo(body.AsSpan(offset + 4));
        offset += 4 + bank0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), checked((uint)bank1));
        if (bank1 > 0) quickChat1.AsSpan().CopyTo(body.AsSpan(offset + 4));
        return body;
    }

    static int BankLength(ImmutableArray<byte> bank)
    {
        if (bank.IsDefault) return 0;
        if (bank.Length != DfoServer.Game.Settings.ClientSettingsService.QuickChatLength)
            throw new InvalidDataException("快捷聊天银行回放长度不匹配");
        return bank.Length;
    }

    public static byte[] CharacterOptions(ImmutableArray<byte> options)
    {
        if (options.IsDefault || options.Length != 512)
            throw new InvalidDataException("角色设置回放长度不匹配");
        byte[] body = new byte[checked(options.Length + 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(body, checked((uint)options.Length));
        options.AsSpan().CopyTo(body.AsSpan(4));
        return body;
    }
}
