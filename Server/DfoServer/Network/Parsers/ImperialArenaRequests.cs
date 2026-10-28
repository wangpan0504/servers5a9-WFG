using System.Buffers.Binary;
using DfoServer.Game.ImperialArena;

namespace DfoServer.Network.Parsers;

internal static class ImperialArenaRequests
{
    // 0176D5E0遍历两名角色，每名仅写server/index各一字节；不是再战的75字节三队设置。
    public static (byte FirstServer, byte FirstIndex, byte SecondServer, byte SecondIndex) Configuration(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4) throw new InvalidDataException("帝国竞技场编队正文必须是四字节");
        return (body[0], body[1], body[2], body[3]);
    }

    public static ImperialArenaMode Purchase(ReadOnlySpan<byte> body)
    {
        if (body.Length != 1 || body[0] is not (1 or 2)) throw new InvalidDataException("帝国竞技场付费模式无效");
        return (ImperialArenaMode)body[0];
    }

    public static uint Entry(ReadOnlySpan<byte> body)
    {
        if (body.Length != 6 || BinaryPrimitives.ReadUInt16LittleEndian(body[4..]) != 0)
            throw new InvalidDataException("帝国竞技场选图正文必须是目标加零u16");
        uint dungeon = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (dungeon is not (320 or 336)) throw new InvalidDataException("帝国竞技场副本不匹配");
        return dungeon;
    }
}
