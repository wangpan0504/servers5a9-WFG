using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class InventoryExpansionPackets
{
    public static byte[] Ack(InventoryResult result)
    {
        // 公共ACK消费者读success:u8，失败再读error:u16；静态注册未发现430专用callback，使用已确认的公共ACK合同。
        if (result.Accepted) return [1];
        byte[] body = [0, 0, 0];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), result.Error);
        return body;
    }
}
