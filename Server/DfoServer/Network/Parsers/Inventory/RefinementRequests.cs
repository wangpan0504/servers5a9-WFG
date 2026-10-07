using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class RefinementRequests
{
    public static RefinementRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length < 15)
            throw new InvalidDataException("Client锻造请求缺少名称或确认标志");
        int nameLength = BinaryPrimitives.ReadInt32LittleEndian(body[9..]);
        if (nameLength <= 0 || nameLength != body.Length - 14 || body[^1] > 2)
            throw new InvalidDataException("Client锻造请求名称长度或确认标志无效");
        // 029DAC38..029DAC7E附带界面对象名称和确认状态；不把客户端文字当作角色身份或材料资格。
        return new(new((InventoryListType)body[0], BinaryPrimitives.ReadInt16LittleEndian(body[1..])),
            BinaryPrimitives.ReadInt32LittleEndian(body[3..]), BinaryPrimitives.ReadInt16LittleEndian(body[7..]), body[^1]);
    }
}
