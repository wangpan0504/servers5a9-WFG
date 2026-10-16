using System.Buffers.Binary;
using System.Collections.Immutable;

namespace DfoServer.Network.Builders;

internal static class SettingsPackets
{
    public static byte[] Emotion(ushort value, bool success = true)
    {
        byte[] body = new byte[3];
        body[0] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), value);
        return body;
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

    public static byte[] GameOptions(ImmutableArray<byte> options)
    {
        if (options.IsDefault || options.Length != 204)
            throw new InvalidDataException("主设置回放长度不匹配");
        // 主设置后保留两个空快捷聊天银行的长度字段。
        byte[] body = new byte[checked(options.Length + 12)];
        BinaryPrimitives.WriteUInt32LittleEndian(body, checked((uint)options.Length));
        options.AsSpan().CopyTo(body.AsSpan(4));
        return body;
    }

    public static byte[] GameOptionsInitialization()
    {
        // 匹配客户端将全部 7FFF 识别为未设置，采用原生默认并完成模型初始化。
        byte[] body = new byte[216];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 204);
        for (int offset = 4; offset < 208; offset += 2)
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(offset), 0x7FFF);
        return body;
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
