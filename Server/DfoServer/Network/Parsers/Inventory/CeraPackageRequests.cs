using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class CeraPackageRequests
{
    public static CeraPackageOpenRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length < 3)
            throw new InvalidDataException("客户端礼包请求过短");
        int count = body[2];
        if (body.Length != 3 + checked(count * 5))
            throw new InvalidDataException("客户端礼包选择段长度无效");
        short slot = BinaryPrimitives.ReadInt16LittleEndian(body);
        if (slot < 0)
            throw new InvalidDataException("客户端礼包槽位无效");
        var choices = new CeraPackageChoice[count];
        var seen = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int offset = 3 + i * 5;
            int itemId = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            if (itemId <= 0 || !seen.Add(itemId))
                throw new InvalidDataException("客户端礼包选择物品无效");
            choices[i] = new(itemId, body[offset + 4]);
        }
        return new(slot, choices);
    }
}
