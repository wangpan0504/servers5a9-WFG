using DfoServer.Game.Inventory.Models;
using System.Buffers.Binary;

namespace DfoServer.Network.Builders.Inventory;

internal static class UpgradeChroniclePackets
{
    // 成功形态已实测为单字节 0x01（客户端回显 Result : Ok）。失败形态没有参考样本，
    // 先按同类制作命令的「0 + u16 错误码」给出，等实机核对后再定稿。
    public static byte[] Result(InventoryResult result)
    {
        if (result.Accepted)
            return [1];
        var body = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), result.Error);
        return body;
    }
}
