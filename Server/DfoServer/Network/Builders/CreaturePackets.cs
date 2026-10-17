using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders;

internal static class CreaturePackets
{
    public static byte[] Actor(ushort actorId) => [(byte)actorId, (byte)(actorId >> 8)];

    public static byte[] State(long uid, int satiety)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        // 0237DC10读取两个i32，不能沿用旧版的i32 UID加u8饱食度。
        writer.Write(checked((int)uid));
        writer.Write(satiety);
        return stream.ToArray();
    }

    public static byte[] Experience(CreatureDetail detail)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(detail.Level);
        writer.Write(detail.Mode);
        writer.Write(detail.Experience);
        if (detail.Mode == 1)
        {
            writer.Write(detail.Mode1Field0);
            writer.Write(detail.Mode1Field1);
        }
        return stream.ToArray();
    }

    public static byte[] Evolution(ushort species, ushort actorId)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        // 0237DC50的种类及角色引用均为u16，高版本种类不能截断为旧版u8。
        writer.Write(species);
        writer.Write(actorId);
        return stream.ToArray();
    }

    public static byte[] Renamed(ushort actorId, string name)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(actorId);
        PacketEncoding.WriteString(writer, name);
        return stream.ToArray();
    }

    public static byte[] RenameAck(InventorySlot source, bool accepted, ushort error = 0) => accepted
        ? [1, (byte)source.Index, (byte)(source.Index >> 8), (byte)source.List]
        : [0, (byte)error, (byte)(error >> 8)];

    public static byte[] Message(byte type, ushort actorId, string text)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(type);
        writer.Write(actorId);
        writer.Write((byte)0);
        PacketEncoding.WriteString(writer, text);
        return stream.ToArray();
    }
}
