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
        // 029CABC0普通C2S43精确20B，offset4固定零；038521C0的18B变体不在本合同内。
        // 00427D20的opcode43分支不追加MD5尾，不能接受截尾或额外4B检查值。
        if (body.Length != 20 || body[4] != 0)
            return false;

        request = new(BinaryPrimitives.ReadUInt32LittleEndian(body), body[5],
            BinaryPrimitives.ReadUInt16LittleEndian(body[6..]), BinaryPrimitives.ReadUInt16LittleEndian(body[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[10..]), BinaryPrimitives.ReadUInt16LittleEndian(body[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[14..]), BinaryPrimitives.ReadUInt16LittleEndian(body[16..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[18..]));
        return true;
    }
}
