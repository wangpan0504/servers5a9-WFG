using System.Buffers.Binary;
using System.Collections.Immutable;

namespace DfoServer.Network.Builders;

internal static class SettingsPackets
{
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
