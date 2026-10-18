using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class EquipmentEmblemPackets
{
    public const ushort InvalidRequest = 4;

    public static byte[] Used(IReadOnlyList<EquipmentEmblemRemaining> materials)
    {
        if (materials.Count is < 1 or > 2 || materials.Any(row => row.Slot < 0 || row.Count < 0) ||
            materials.Select(row => row.Slot).Distinct().Count() != materials.Count)
            throw new InvalidDataException("普通装备徽章成功回执材料差量无效");
        using var stream = new MemoryStream(2 + materials.Count * 6);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write((byte)materials.Count);
        foreach (var material in materials)
        {
            writer.Write(material.Slot);
            writer.Write(material.Count);
        }
        return stream.ToArray();
    }

    public static byte[] SocketOpened(short targetSlot, short materialSlot)
    {
        if (targetSlot < 0 || materialSlot < 0)
            throw new InvalidDataException("普通装备开孔成功回执槽位无效");
        using var stream = new MemoryStream(5);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(targetSlot);
        writer.Write(materialSlot);
        return stream.ToArray();
    }

    // 公共应答分派先读success:u8，失败分支再读error:u16；不能沿用90版的单字节错误。
    public static byte[] Failure(ushort error = InvalidRequest)
    {
        using var stream = new MemoryStream(3);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0);
        writer.Write(error);
        return stream.ToArray();
    }
}
