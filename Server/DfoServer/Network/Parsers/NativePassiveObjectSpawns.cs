using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Network.Parsers;

// Native21 的模板与实例身份分别保留；合法结构不代表服务端已经批准生成。
internal sealed record NativePassiveObjectSpawn(uint Template, ushort ObjectKind, ushort ObjectKey,
    byte PositionMode, byte? Direction, int? X, int? Y, int? Z, ImmutableArray<byte> Body);

internal sealed record NativePassiveObjectSpawnBatch(ushort SourceKind, ushort SourceKey,
    ushort ParentKind, ushort ParentKey, ushort RoomX, ushort RoomY, uint Mode, uint Tick,
    ImmutableArray<NativePassiveObjectSpawn> Rows, bool HasRoomPrefix);

internal static class NativePassiveObjectSpawns
{
    public static bool TryRead(NativeGameplayRecord record, DungeonPoint room, out NativePassiveObjectSpawnBatch? batch)
    {
        batch = null;
        if (!TryPayload(record, UdpPacketType.CREATE_PASSIVEOBJECT, out var plain)) return false;
        return TryReadPlain(plain, room, out batch);
    }

    // 私有观测桥接已由游戏帧校验；只共用结构解析，不伪造Native中继或CRC。
    internal static bool TryReadPlain(ReadOnlySpan<byte> plain, DungeonPoint room, out NativePassiveObjectSpawnBatch? batch)
    {
        batch = null;
        bool direct = TryBody(plain, false, out var first);
        bool prefixed = plain.Length >= 2 && plain[0] == room.X && plain[1] == room.Y &&
            TryBody(plain[2..], true, out _);
        if (direct == prefixed) return false;
        if (direct) batch = first;
        else TryBody(plain[2..], true, out batch);
        return true;
    }

    internal static bool TryPayload(NativeGameplayRecord record, UdpPacketType opcode, out byte[] plain)
    {
        plain = [];
        var wire = record.Wire.Span;
        if (record.Kind is not (1 or 2 or 8) || wire.Length < 16 || wire.Length > NativeGameplayRecords.MaximumLength ||
            BinaryPrimitives.ReadUInt16LittleEndian(wire[5..]) != wire.Length - 9) return false;
        var data = wire[9..];
        if (data[0] > 2 || BinaryPrimitives.ReadUInt16LittleEndian(data[1..]) != (ushort)opcode) return false;
        plain = data[7..].ToArray();
        NativeGameplayRecords.Transform(plain, false);
        return GameFrame.Crc(plain) == BinaryPrimitives.ReadUInt32LittleEndian(data[3..]);
    }

    // 036BE224..036BF407：两处诊断标志分别追加10/6个u32；每行可带116B结构。
    static bool TryBody(ReadOnlySpan<byte> body, bool prefixed, out NativePassiveObjectSpawnBatch? batch)
    {
        batch = null;
        if (body.Length < 36) return false;
        int at = 25 + (body[24] == 0 ? 0 : 40);
        if (body.Length - at < 11) return false;
        ushort roomX = BinaryPrimitives.ReadUInt16LittleEndian(body[at..]);
        ushort roomY = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 2)..]);
        uint tick = BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 4)..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 9)..]);
        at += 11;
        if (count > (body.Length - at) / 46) return false;
        var rows = ImmutableArray.CreateBuilder<NativePassiveObjectSpawn>(count);
        for (int index = 0; index < count; index++)
        {
            int start = at;
            if (body.Length - at < 5) return false;
            uint template = BinaryPrimitives.ReadUInt32LittleEndian(body[at..]);
            at += 5 + (body[at + 4] == 0 ? 0 : 24);
            if (body.Length - at < 11) return false;
            ushort kind = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 4)..]);
            ushort key = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 6)..]);
            at += 11 + (body[at + 10] == 0 ? 0 : 116);
            if (body.Length - at < 5) return false;
            byte positionMode = body[at + 4];
            at += 5;
            int positionLength = positionMode == 1 ? 8 : 13;
            if (body.Length - at < positionLength) return false;
            byte? direction = positionMode == 1 ? null : body[at];
            int? x = positionMode == 1 ? null : BinaryPrimitives.ReadInt32LittleEndian(body[(at + 1)..]);
            int? y = positionMode == 1 ? null : BinaryPrimitives.ReadInt32LittleEndian(body[(at + 5)..]);
            int? z = positionMode == 1 ? null : BinaryPrimitives.ReadInt32LittleEndian(body[(at + 9)..]);
            at += positionLength;
            if (body.Length - at < 2) return false;
            int blobLength = BinaryPrimitives.ReadUInt16LittleEndian(body[at..]);
            at += 2;
            if (body.Length - at < blobLength + 15) return false;
            at += blobLength;
            int extraLength = body[at + 2] == 0 ? 0 : 4;
            if (body.Length - at < 15 + extraLength) return false;
            at += 15 + extraLength;
            rows.Add(new(template, kind, key, positionMode, direction, x, y, z,
                ImmutableArray.CreateRange(body[start..at].ToArray())));
        }
        if (at != body.Length) return false;
        batch = new(BinaryPrimitives.ReadUInt16LittleEndian(body[3..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[5..]), BinaryPrimitives.ReadUInt16LittleEndian(body[7..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[9..]), roomX, roomY,
            BinaryPrimitives.ReadUInt32LittleEndian(body[20..]), tick, rows.MoveToImmutable(), prefixed);
        return true;
    }
}
