using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class CeraPackagePackets
{
    public static byte[] Opened(InventoryResult result, CeraPackageOpenRequest? request)
    {
        if (!result.Accepted)
            return [0, 4, 0];
        using var stream = new MemoryStream(3);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(request!.Slot);
        return stream.ToArray();
    }
}
