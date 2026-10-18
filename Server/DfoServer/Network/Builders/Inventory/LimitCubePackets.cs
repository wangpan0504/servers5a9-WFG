using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class LimitCubePackets
{
    public const int SuccessSize = 12;

    public static byte[] Success(LimitCubeReceipt receipt)
    {
        if (receipt.ResultItemId <= 0 || receipt.ResultCount == 0 ||
            receipt.ResultSlot.List != InventoryListType.Main || receipt.ResultSlot.Index < 0)
            throw new InvalidDataException("变更券成功回执无效");
        using var stream = new MemoryStream(SuccessSize);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(receipt.ResultItemId);
        writer.Write(receipt.ResultCount);
        writer.Write((uint)receipt.ResultSlot.Index);
        writer.Write((byte)1);
        byte[] body = stream.ToArray();
        if (body.Length != SuccessSize)
            throw new InvalidDataException("变更券成功回执宽度变化");
        return body;
    }

    public static byte[] Failure(ushort error = 1) =>
        CargoPackets.Ack(InventoryResult.Reject(error));
}
