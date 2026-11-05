using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal readonly record struct MoveToAccountCargoRequest(InventoryListType List, short Slot, int Count);

internal static class CargoRequests
{
    public static void AccountUpgrade(ReadOnlySpan<byte> body)
    {
        // 03948BC0 / 039499D2：305、306 只发送命令头。
        if (!body.IsEmpty)
            throw new InvalidDataException("Client账号仓库升级请求宽度");
    }
    public static int Gold(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4)
            throw new InvalidDataException("Client仓库金币请求宽度");
        return BinaryPrimitives.ReadInt32LittleEndian(body);
    }
    public static short PersonalUpgrade(ReadOnlySpan<byte> body)
    {
        // 02E55FC0 先按工具模板查槽，再在02E56016写入u16槽号。
        if (body.Length != 2)
            throw new InvalidDataException("Client个人仓库升级请求宽度");
        return BinaryPrimitives.ReadInt16LittleEndian(body);
    }

    public static MoveToAccountCargoRequest MoveToAccountCargo(ReadOnlySpan<byte> body) => body.Length switch
    {
        2 => new(InventoryListType.Main, BinaryPrimitives.ReadInt16LittleEndian(body), 0),
        3 => new((InventoryListType)body[0], BinaryPrimitives.ReadInt16LittleEndian(body[1..]), 0),
        6 => new(InventoryListType.Main, BinaryPrimitives.ReadInt16LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body[2..])),
        7 => new((InventoryListType)body[0], BinaryPrimitives.ReadInt16LittleEndian(body[1..]), BinaryPrimitives.ReadInt32LittleEndian(body[3..])),
        _ => throw new InvalidDataException("Client移入账号仓库请求宽度")
    };
}
