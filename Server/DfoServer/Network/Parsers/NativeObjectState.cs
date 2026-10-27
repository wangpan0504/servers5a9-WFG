using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Network.Parsers;

// 原生5的结构事实，不代表对象实例归属、合法受击或设施完成证明。
internal sealed record NativeObjectState(ushort ObjectKind, ushort Instance, byte State, int Tick,
    ImmutableArray<int> Values, byte FlagA, int ExtraLength, byte FlagB, ushort OpaqueA, ushort OpaqueB,
    ushort Random, bool HasRoomPrefix);

internal static class NativeObjectStates
{
    public static bool TryRead(NativeGameplayRecord record, DungeonPoint room, out NativeObjectState? state)
    {
        state = null;
        var wire = record.Wire.Span;
        if (record.Kind is not (1 or 2 or 8) || wire.Length < 16 || wire.Length > NativeGameplayRecords.MaximumLength ||
            BinaryPrimitives.ReadUInt16LittleEndian(wire[5..]) != wire.Length - 9) return false;
        var data = wire[9..];
        if (data[0] > 2 || BinaryPrimitives.ReadUInt16LittleEndian(data[1..]) != 5) return false;
        byte[] plain = data[7..].ToArray();
        NativeGameplayRecords.Transform(plain, false);
        if (GameFrame.Crc(plain) != BinaryPrimitives.ReadUInt32LittleEndian(data[3..])) return false;
        bool direct = TryBody(plain, false, out var first);
        bool prefixed = plain.Length >= 2 && plain[0] == room.X && plain[1] == room.Y &&
            TryBody(plain.AsSpan(2), true, out _);
        // 房间资源决定是否携带XY；未闭合资源标记时只观测唯一解释，不猜歧义布局。
        if (direct == prefixed) return false;
        if (direct) state = first;
        else TryBody(plain.AsSpan(2), true, out state);
        return true;
    }

    static bool TryBody(ReadOnlySpan<byte> body, bool prefixed, out NativeObjectState? state)
    {
        state = null;
        if (body.Length < 21) return false;
        int vectorLength = BinaryPrimitives.ReadUInt16LittleEndian(body[9..]);
        if (vectorLength > body.Length - 21 || !TryIntegers(body.Slice(11, vectorLength), out var values)) return false;
        int at = 11 + vectorLength;
        byte flagA = body[at++];
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(body[at..]);
        at += 2;
        if (extraLength != body.Length - at - 7) return false;
        at += extraLength;
        byte flagB = body[at++];
        state = new(BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[2..]),
            body[4], BinaryPrimitives.ReadInt32LittleEndian(body[5..]), values, flagA, extraLength, flagB,
            BinaryPrimitives.ReadUInt16LittleEndian(body[at..]), BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 2)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 4)..]), prefixed);
        return true;
    }

    // 02C20D20为高组在前的7位整数，不是ZigZag。严格边界是本服观测契约，原客户端解码较宽松。
    internal static bool TryIntegers(ReadOnlySpan<byte> bytes, out ImmutableArray<int> values)
    {
        var result = ImmutableArray.CreateBuilder<int>();
        uint value = 0;
        int groups = 0;
        foreach (byte current in bytes)
        {
            if (groups == 0 && current == 0x80 || groups == 5 || value > (uint.MaxValue >> 7))
            { values = []; return false; }
            value = (value << 7) | (uint)(current & 0x7F);
            groups++;
            if ((current & 0x80) != 0) continue;
            result.Add(unchecked((int)value));
            value = 0;
            groups = 0;
        }
        values = groups == 0 ? result.ToImmutable() : [];
        return groups == 0;
    }
}
