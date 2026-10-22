using DfoServer.Game.Inventory.Models;
using System.Buffers.Binary;

namespace DfoServer.Network.Builders.Inventory;

internal static class CompoundPackets
{
    // ACK25 成功体：success:u8=1 + changedCount:u8=0 + rewardCount:u8=0 + 尾部u16=0，共4字节。
    // 语义对齐参考端（它发 01 00 00），本版客户端在货币列表后还要读 u16，故补一个零尾部。
    // 长度是实机硬约束：列表为空需 4B（发 3B 闪退）、一行需 12B（发 10B 闪退）。
    // 已知限制：结果窗口只显示标题「组合成功」，不显示产物。改用变更行(带/不带槽位)或把产物放进
    // 末尾货币列表都无法让窗口显示内容，说明窗口数据源不是本 ACK 的行列表。
    // 失败体为 success:u8=0 + error:u16。
    public static byte[] Result(InventoryResult result)
    {
        if (result.Accepted)
            return [1, 0, 0, 0];
        var failure = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(failure.AsSpan(1), result.Error);
        return failure;
    }
}
