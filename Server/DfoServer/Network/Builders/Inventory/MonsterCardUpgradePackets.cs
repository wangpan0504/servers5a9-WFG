using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class MonsterCardUpgradePackets
{
    // 00948160：公共成功标记后读取升级结果、模板编号、卡片等级；库存另由NOTI14同步。
    public static byte[] Success(MonsterCardUpgradeReceipt receipt)
    {
        if (receipt.ItemId <= 0)
            throw new InvalidDataException("卡片升级回执缺少模板编号");
        byte[] body = new byte[7];
        body[0] = 1;
        body[1] = receipt.Upgraded ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), receipt.ItemId);
        body[6] = receipt.Level;
        return body;
    }

    public static byte[] Failure(ushort error = 22)
    {
        byte[] body = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), error);
        return body;
    }
}
