using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class CubeContractPackets
{
    // 024CEAF0读取成功包的两个字节；024CFF00以原始通知读取同样的选择，FF是取消而非黑色。
    public static byte[] Result(CubeContractState state, bool accepted, ushort error = 1) => accepted
        ? [1, state.Selected, unchecked((byte)state.CubeType)]
        : CargoPackets.Ack(InventoryResult.Reject(error));
    public static byte[] Selection(CubeContractState state) => [state.Selected, unchecked((byte)state.CubeType)];
}
