using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Network.Builders;

internal static class RaidCrossChannelPackets
{
    public static byte[] Failure => [0, 0x13, 0];

    // 023D5FB0读59B；身份空余两字节保留0，不能将显示资料解释为IP/端口。
    public static byte[] Request(Character character, ChannelIdentity channel, ushort fatigue, ushort stamina,
        ushort antiMagic, ReadOnlySpan<byte> actor)
    {
        if (actor.Length < 123) throw new InvalidDataException("跨频道摘要缺少完整Actor状态");
        byte[] body = new byte[59];
        BinaryPrimitives.WriteUInt16LittleEndian(body, character.Id);
        body[4] = channel.ServerIndex; body[5] = channel.Number;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(6), checked((uint)character.Inventory.ReviveCoins));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(10), fatigue);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(12), stamina);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), antiMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(16), character.Level);
        body[18] = character.Job; body[19] = character.GrowType;
        CharacterName.Encoding.GetEncoder().Convert(character.Name.AsSpan(), body.AsSpan(20, 29), true,
            out _, out _, out _);
        // 与原Actor中同一显示字段对齐；末六字节为u32及两个u8，不盲填身份状态。
        body[50] = actor[5 + 79]; body[52] = actor[5 + 84]; body[55] = actor[5 + 85];
        body[53] = actor[^6]; body[54] = actor[^2]; body[58] = actor[^1];
        actor.Slice(5 + 114, 2).CopyTo(body.AsSpan(56));
        return body;
    }

    // 023D11A0读取6B，以目标身份查Actor/攻坚目录后切频道；未赋义的中间两字节保留0。
    public static byte[] Response(ushort character, ChannelIdentity channel)
    {
        byte[] body = new byte[6];
        body[0] = channel.ServerIndex;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), character);
        body[5] = channel.Number;
        return body;
    }
}
