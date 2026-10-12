using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class BoosterRequests
{
    public static short ReadSourceSlot(ReadOnlySpan<byte> body)
    {
        if (body.Length < 6) throw new InvalidDataException("礼包请求源字段未完整闭合");
        return BinaryPrimitives.ReadInt16LittleEndian(body);
    }
    // 普通物品选择的两段附加选项计数均为0；时装等扩展不能按此布局截断。
    public static bool IsItemSelection(ReadOnlySpan<byte> body) => body.Length >= 10 && (body.Length - 10) % 4 == 0 &&
        body[^2] == 0 && body[^1] == 0;

    public static BoosterOpenRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 6 && !IsItemSelection(body))
            throw new InvalidDataException("Client礼包请求长度或附加选项无效");
        BoosterSelectionRequest? selection = null;
        if (body.Length != 6)
        {
            var ids = new int[(body.Length - 10) / 4];
            for (int i = 0; i < ids.Length; i++)
                ids[i] = BinaryPrimitives.ReadInt32LittleEndian(body[(8 + i * 4)..]);
            selection = new(body[6], body[7], ids);
        }
        return new(BinaryPrimitives.ReadInt16LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body[2..]), selection);
    }
}
