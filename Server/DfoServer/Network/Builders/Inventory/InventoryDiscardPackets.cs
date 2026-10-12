using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders.Inventory;

internal static class InventoryDiscardPackets
{
    // ACK47 的数量是实际删除量，不是源槽剩余量。
    public static byte[] Accepted(InventoryDiscardRequest request)
    {
        byte[] body = new byte[8];
        body[0] = 1;
        body[1] = request.List;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), request.Slot);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), request.Count);
        return body;
    }

    public static byte[] Rejected(byte list, ushort error) => [0, (byte)error, (byte)(error >> 8), list];

    public static byte[] Ground(ushort actor, DungeonGroundObject ground)
    {
        if (actor == 0 || ground.InventoryOrigin is null || ground.IsGold ||
            ground.Core.Kind is ItemKind.Avatar or ItemKind.Creature)
            throw new InvalidDataException("地面丢弃通知缺少完整普通实例");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(actor);
        writer.Write(ground.Reservation.X);
        writer.Write(ground.Reservation.Y);
        writer.Write(ground.Reservation.GroundKey);
        ItemListProtocolWriter.Write(writer, ground.Reservation.RecordSlot, ground.Core);
        writer.Write((byte)0);
        writer.Write(actor);
        return stream.ToArray();
    }
}
