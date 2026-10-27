using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class ChronicleEnchantRequests
{
    public static ChronicleEnchantRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 14) throw new InvalidDataException("气息赋予请求必须为14字节");
        short material = BinaryPrimitives.ReadInt16LittleEndian(body), target = BinaryPrimitives.ReadInt16LittleEndian(body[7..]);
        int materialId = BinaryPrimitives.ReadInt32LittleEndian(body[2..]), targetId = BinaryPrimitives.ReadInt32LittleEndian(body[9..]);
        if (material < 0 || target < 0 || materialId <= 0 || targetId <= 0) throw new InvalidDataException("气息请求物品身份无效");
        return new(material, materialId, new((InventoryListType)body[6], target), targetId, body[13]);
    }
}
