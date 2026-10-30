using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class AvatarEmblemPackets
{
    public static byte[] Used(IReadOnlyList<AvatarEmblemRemaining> materials)
    {
        if (materials.Count is < 1 or > 5 || materials.Any(row => row.Slot < 0 || row.Count < 0) ||
            materials.Select(row => row.Slot).Distinct().Count() != materials.Count)
            throw new InvalidDataException("装扮徽章回执材料无效");
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

    public static byte[] Failure(ushort error = 4) => [(byte)0, (byte)error, (byte)(error >> 8)];
}
