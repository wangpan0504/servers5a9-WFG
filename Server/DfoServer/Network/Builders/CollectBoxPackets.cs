using DfoServer.Game.CollectBox;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders;

internal static class CollectBoxPackets
{
    public static byte[] Select(byte box, CollectBoxResult result)
    {
        if (!result.Accepted)
            return Failure(result.Error);
        return [box, box];
    }

    public static byte[] Mutation(CollectBoxResult result)
    {
        if (!result.Accepted)
            return Failure(result.Error);
        return [1];
    }

    public static byte[] Extension(CollectBoxExtendRequest request, CollectBoxResult result)
    {
        if (!result.Accepted)
            return Failure(result.Error);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(request.Box);
        writer.Write(request.ItemId);
        return stream.ToArray();
    }

    public static byte[] State(CollectBoxEntry box, CollectBoxState state, byte selectedBox, long now)
    {
        var items = state.Items.Where(item => item.Box == box.Index &&
                box.TrySlot(item.ItemId, out var configured) && configured.PhysicalSlot == item.PhysicalSlot)
            .OrderBy(item => item.PhysicalSlot).Select(item => item.ItemId).ToArray();
        if (items.Length > byte.MaxValue)
            throw new InvalidDataException("宝珠收集箱通知超过客户端u8物品计数");

        long? expiration = box.ResolveExpiration(state.Expiration(box.Index));
        bool permanent = expiration is null;
        uint remaining = expiration switch
        {
            null => 0,
            long value when value <= now => uint.MaxValue,
            long value => checked((uint)Math.Min(value - now, int.MaxValue))
        };

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(box.Index);
        writer.Write(box.Index == selectedBox ? (byte)1 : (byte)0);
        writer.Write(remaining);
        writer.Write(permanent ? (byte)1 : (byte)0);
        writer.Write(checked((byte)items.Length));
        foreach (int itemId in items)
            writer.Write(itemId);
        return stream.ToArray();
    }

    static byte[] Failure(ushort error)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0);
        writer.Write(error);
        return stream.ToArray();
    }
}
