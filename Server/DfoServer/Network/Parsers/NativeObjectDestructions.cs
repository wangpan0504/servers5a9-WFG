using System.Buffers.Binary;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Network.Parsers;

internal sealed record NativeObjectDestruction(ushort ObjectKind, ushort ObjectKey, DungeonPoint Room, bool HasRoomPrefix);

internal static class NativeObjectDestructions
{
    // 02A5FC90 写、036C0DCF 读：kind/key/u8 x/u8 y；销毁不等于已执行Last行为。
    public static bool TryRead(NativeGameplayRecord record, DungeonPoint room, out NativeObjectDestruction? destruction)
    {
        destruction = null;
        if (!NativePassiveObjectSpawns.TryPayload(record, UdpPacketType.DESTROY_OBJECT, out var plain)) return false;
        return TryReadPlain(plain, room, out destruction);
    }

    internal static bool TryReadPlain(ReadOnlySpan<byte> plain, DungeonPoint room, out NativeObjectDestruction? destruction)
    {
        destruction = null;
        bool prefixed = plain.Length == 8;
        if (plain.Length != 6 && !prefixed || prefixed && (plain[0] != room.X || plain[1] != room.Y)) return false;
        var body = plain[(prefixed ? 2 : 0)..];
        if (body[4] != room.X || body[5] != room.Y) return false;
        destruction = new(BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[2..]),
            new(body[4], body[5]), prefixed);
        return true;
    }
}
