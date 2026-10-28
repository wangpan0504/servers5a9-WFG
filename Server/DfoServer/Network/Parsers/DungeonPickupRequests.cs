using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

// Context和三个Token保持原始值；坐标与Token不提供资产资格或奖励数量。
internal readonly record struct DungeonPickupRequest(uint GroundObjectKey, byte Context,
    ushort PlayerX, ushort PlayerY, ushort Token0, ushort GroundX, ushort GroundY, ushort Token1, ushort Token2);

internal static class DungeonPickupRequests
{
    public static bool TryParseOrdinary(ReadOnlySpan<byte> body, out DungeonPickupRequest request)
    {
        request = default;
        // C2S43普通拾取两式同义：20B长式key:u32@0 + form=0@4 + context@5 + 7×u16@6（029CABC0）；
        // 客户端另有18B短式key:u16@0 + form=1@2 + context@3 + 7×u16@4（038521C0实机键15185/15292/15329）。
        // 两式尾段起点都等于form偏移+2，坐标与三个Token保持同一组布局；短式键域只有u16，由form字节区分。
        // 00427D20的opcode43分支不追加MD5尾，不能接受截尾或额外4B检查值。
        uint key; byte context; int tail;
        if (body.Length == 20 && body[4] == 0) { key = BinaryPrimitives.ReadUInt32LittleEndian(body); context = body[5]; tail = 6; }
        else if (body.Length == 18 && body[2] == 1) { key = BinaryPrimitives.ReadUInt16LittleEndian(body); context = body[3]; tail = 4; }
        else return false;

        request = new(key, context,
            BinaryPrimitives.ReadUInt16LittleEndian(body[tail..]), BinaryPrimitives.ReadUInt16LittleEndian(body[(tail + 2)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[(tail + 4)..]), BinaryPrimitives.ReadUInt16LittleEndian(body[(tail + 6)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[(tail + 8)..]), BinaryPrimitives.ReadUInt16LittleEndian(body[(tail + 10)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[(tail + 12)..]));
        return true;
    }
}
