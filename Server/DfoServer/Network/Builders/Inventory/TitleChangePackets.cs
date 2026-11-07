using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class TitleChangePackets
{
    // ARAD.exe 02358F90：
    // 失败时读取 result:u8(0) + errorCode:u16；
    // 成功时读取 result:u8(1) + isSuccess:u8 + resultTitleItemId:u32 + usedThreadItemId:u32。
    public static byte[] Ack(InventoryResult result, TitleChangeOutcome outcome)
    {
        if (!result.Accepted)
            return [0, (byte)result.Error, (byte)(result.Error >> 8)];
        byte[] bytes = new byte[10];
        bytes[0] = 1;
        bytes[1] = outcome.IsSuccess ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2, 4), outcome.ResultTitleItemId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(6, 4), outcome.UsedThreadItemId);
        return bytes;
    }
}
