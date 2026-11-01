using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Network.Parsers;

// 仅保存原生22的结构事实；来源FFFF和对象归属留给正式实例领域验证。
internal sealed record NativeMonsterSpawn(uint MonsterId, byte Level, ushort ObjectKind, ushort ObjectKey,
    byte Direction, int X, int Y, int Z, byte OpaqueFlag, ImmutableArray<byte> OpaqueBlob,
    byte OpaqueStructureFlag, ImmutableArray<byte> OpaqueStructure, uint OpaqueValue, ushort OpaqueWord,
    byte TrailingA, byte TrailingB, ImmutableArray<byte> Statuses);

internal sealed record NativeMonsterSpawnBatch(byte ReportFlag, byte Mode, ushort? ModeValue,
    ushort SourceKind, ushort SourceKey, ushort SourceAltA, ushort SourceAltB,
    byte FlagA, byte FlagB, byte FlagC, uint OpaqueInheritedValue, ushort Team, uint Tick,
    ImmutableArray<NativeMonsterSpawn> Rows, bool HasRoomPrefix);

internal static class NativeMonsterSpawns
{
    public static bool TryRead(NativeGameplayRecord record, DungeonPoint room, out NativeMonsterSpawnBatch? batch)
    {
        batch = null;
        var wire = record.Wire.Span;
        if (record.Kind is not (1 or 2 or 8) || wire.Length < 16 || wire.Length > NativeGameplayRecords.MaximumLength ||
            BinaryPrimitives.ReadUInt16LittleEndian(wire[5..]) != wire.Length - 9) return false;
        var data = wire[9..];
        if (data[0] > 2 || BinaryPrimitives.ReadUInt16LittleEndian(data[1..]) != (ushort)UdpPacketType.CREATE_MONSTER)
            return false;
        byte[] plain = data[7..].ToArray();
        NativeGameplayRecords.Transform(plain, false);
        if (GameFrame.Crc(plain) != BinaryPrimitives.ReadUInt32LittleEndian(data[3..])) return false;
        bool direct = TryBody(plain, false, out var first);
        bool prefixed = plain.Length >= 2 && plain[0] == room.X && plain[1] == room.Y &&
            TryBody(plain.AsSpan(2), true, out _);
        // 有无XY前缀必须只有一种完整解释，不能用猜测后的实例键登记对象。
        if (direct == prefixed) return false;
        if (direct) batch = first;
        else TryBody(plain.AsSpan(2), true, out batch);
        return true;
    }

    // 02C1CA47..CEEC写入；036C0325..0DCA逐行消费，包括行末状态列表。
    static bool TryBody(ReadOnlySpan<byte> body, bool prefixed, out NativeMonsterSpawnBatch? batch)
    {
        batch = null;
        if (body.Length < 24) return false;
        int modeLength = body[1] == 1 ? 2 : 0;
        if (body.Length < 24 + modeLength) return false;
        int at = 24 + modeLength;
        int count = body[23 + modeLength];
        if (count > (body.Length - at) / 35) return false;
        var rows = ImmutableArray.CreateBuilder<NativeMonsterSpawn>(count);
        for (int index = 0; index < count; index++)
        {
            var row = body[at..];
            if (row.Length < 35) return false;
            int blobLength = BinaryPrimitives.ReadUInt16LittleEndian(row[23..]);
            if (blobLength > row.Length - 35) return false;
            byte structureFlag = row[25 + blobLength];
            int structureLength = structureFlag == 0 ? 0 : 73;
            int tail = 26 + blobLength + structureLength;
            if (row.Length - tail < 9) return false;
            int statusCount = row[tail + 8];
            if (statusCount > row.Length - tail - 9) return false;
            rows.Add(new(BinaryPrimitives.ReadUInt32LittleEndian(row), row[4],
                BinaryPrimitives.ReadUInt16LittleEndian(row[5..]), BinaryPrimitives.ReadUInt16LittleEndian(row[7..]),
                row[9], BinaryPrimitives.ReadInt32LittleEndian(row[10..]), BinaryPrimitives.ReadInt32LittleEndian(row[14..]),
                BinaryPrimitives.ReadInt32LittleEndian(row[18..]), row[22],
                ImmutableArray.CreateRange(row.Slice(25, blobLength).ToArray()), structureFlag,
                ImmutableArray.CreateRange(row.Slice(26 + blobLength, structureLength).ToArray()),
                BinaryPrimitives.ReadUInt32LittleEndian(row[tail..]), BinaryPrimitives.ReadUInt16LittleEndian(row[(tail + 4)..]),
                row[tail + 6], row[tail + 7], ImmutableArray.CreateRange(row.Slice(tail + 9, statusCount).ToArray())));
            at += tail + 9 + statusCount;
        }
        if (at != body.Length) return false;
        batch = new(body[0], body[1], modeLength == 0 ? null : BinaryPrimitives.ReadUInt16LittleEndian(body[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[(2 + modeLength)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[(4 + modeLength)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[(6 + modeLength)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[(8 + modeLength)..]),
            body[10 + modeLength], body[11 + modeLength], body[12 + modeLength],
            BinaryPrimitives.ReadUInt32LittleEndian(body[(13 + modeLength)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[(17 + modeLength)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[(19 + modeLength)..]), rows.MoveToImmutable(), prefixed);
        return true;
    }
}
