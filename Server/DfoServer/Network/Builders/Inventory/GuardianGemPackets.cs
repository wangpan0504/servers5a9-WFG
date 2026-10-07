using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class GuardianGemPackets
{
    public static byte[] Used(InventoryResult result)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(result.Accepted ? (byte)1 : (byte)0);
        if (!result.Accepted)
            writer.Write(result.Error);
        return stream.ToArray();
    }
}
