using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class AvatarSocketRequests
{
    public static AvatarSocketRequest Read(ReadOnlySpan<byte> body)
    {
        // V31 037A752C..037A7574：目标时装槽、模板及Main材料槽，共8字节。
        if (body.Length != 8) throw new InvalidDataException("时装开孔请求必须为8字节");
        short target = BinaryPrimitives.ReadInt16LittleEndian(body);
        int item = BinaryPrimitives.ReadInt32LittleEndian(body[2..]);
        short material = BinaryPrimitives.ReadInt16LittleEndian(body[6..]);
        if (target < 0 || item <= 0 || material < 0) throw new InvalidDataException("时装开孔槽位或模板无效");
        return new(target, item, material);
    }
}
