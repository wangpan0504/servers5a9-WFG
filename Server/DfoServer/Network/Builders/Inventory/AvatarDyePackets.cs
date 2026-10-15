using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class AvatarDyePackets
{
    public static byte[] Success(short slot, AvatarDetail detail)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(slot);
        ItemListProtocolWriter.WriteAvatarColors(writer, detail);
        return stream.ToArray();
    }

    // 当前仅确认通用失败；不把旧端的业务错误码当成本版498合同。
    public static byte[] Failure() => [0, 0, 0];
}
