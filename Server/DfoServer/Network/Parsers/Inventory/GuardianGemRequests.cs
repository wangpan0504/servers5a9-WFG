using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class GuardianGemRequests
{
    public static GuardianGemUseRequest Use(ReadOnlySpan<byte> body)
    {
        // 0127164E..01271699：831后依次写i32/u16/i32/u8，确认覆盖路径01271732布局相同。
        if (body.Length != 11)
            throw new InvalidDataException("Client守护珠镶嵌请求宽度");
        return new(BinaryPrimitives.ReadInt32LittleEndian(body), BinaryPrimitives.ReadInt16LittleEndian(body[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[6..]), body[10]);
    }
}
