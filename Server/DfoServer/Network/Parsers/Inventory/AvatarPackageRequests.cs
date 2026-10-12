using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class AvatarPackageRequests
{
    public static (short Slot, byte Category, byte Subcategory) ReadHeader(ReadOnlySpan<byte> body)
    {
        if (body.Length < 8) throw new InvalidDataException("装扮礼包请求分类头过短");
        short slot = BinaryPrimitives.ReadInt16LittleEndian(body);
        if (slot < 0) throw new InvalidDataException("装扮礼包源槽无效");
        return (slot, body[6], body[7]);
    }

    // CMD160的物品清单没有独立数量字段；用已确认分类的合法行数辨认后续5B装扮选项。
    public static AvatarPackageOpenRequest Read(ReadOnlySpan<byte> body, int minimumItems, int maximumItems)
    {
        if (body.Length < 10 || minimumItems < 1 || maximumItems < minimumItems)
            throw new InvalidDataException("装扮礼包请求长度或分类范围无效");
        var header = ReadHeader(body);
        AvatarPackageOpenRequest? result = null;
        int last = Math.Min(maximumItems, (body.Length - 10) / 4);
        for (int count = minimumItems; count <= last; count++)
        {
            int at = 8 + count * 4;
            int options = body[at];
            if (body.Length != at + 2 + options * 5 || body[^1] != 0) continue;
            var ids = new int[count];
            for (int i = 0; i < count; i++) ids[i] = BinaryPrimitives.ReadInt32LittleEndian(body[(8 + i * 4)..]);
            var choices = new AvatarPackageChoice[options];
            bool valid = ids.All(id => id > 0);
            var seen = new HashSet<int>();
            for (int i = 0; i < options; i++)
            {
                int offset = at + 1 + i * 5;
                int id = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
                valid &= id > 0 && seen.Add(id) && ids.Contains(id);
                choices[i] = new(id, body[offset + 4]);
            }
            if (!valid) continue;
            if (result is not null) throw new InvalidDataException("装扮礼包选择段存在歧义");
            result = new(header.Slot, BinaryPrimitives.ReadInt32LittleEndian(body[2..]), header.Category, header.Subcategory, ids, choices);
        }
        return result ?? throw new InvalidDataException("装扮礼包选择段未完整闭合");
    }
}
