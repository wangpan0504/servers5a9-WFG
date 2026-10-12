using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class CreatureEggPackets
{
    // 0234D220成功不读附加正文；失败0234D25F读取公共u16。
    public static byte[] Ack(InventoryResult result) => result.Accepted ? [1] :
        [0, (byte)result.Error, (byte)(result.Error >> 8)];
}
