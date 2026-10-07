using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class CargoPackets
{
    public static byte[] Full(InventorySnapshot inventory, InventoryListType list)
    {
        if (list is not (InventoryListType.PersonalCargo or InventoryListType.AccountCargo))
            throw new InvalidDataException("未知仓库列表");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)list);
        writer.Write(list == InventoryListType.PersonalCargo ? inventory.CargoCapacity : inventory.AccountCargo.Capacity);
        if (list == InventoryListType.AccountCargo)
            writer.Write(inventory.AccountCargo.Gold);
        var items = inventory.Items.Where(item => item.Position.List == list).OrderBy(item => item.Position.Index).ToArray();
        writer.Write(checked((ushort)items.Length));
        foreach (var item in items)
            ItemListProtocolWriter.Write(writer, item.Position.Index, item.Core);
        if (list == InventoryListType.PersonalCargo)
            writer.Write((byte)0); // 023BEBB9调用的辅助函数在0267B538继续读取附加条目计数。
        return stream.ToArray();
    }

    public static byte[] Ack(InventoryResult result, int? cargoGold = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(result.Accepted ? (byte)1 : (byte)0);
        if (!result.Accepted)
            writer.Write(result.Error); // 本版公共 ACK 分派先读取 u16 错误码。
        else if (cargoGold.HasValue)
            writer.Write(cargoGold.Value);
        return stream.ToArray();
    }
}
