using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class ConsumableStatePackets
{
    public static byte[] Updates(IReadOnlyList<ConsumableStateUpdate> rows)
    {
        if (rows.Count > byte.MaxValue || rows.Any(row => row.ItemId <= 0 || row.RemainingSeconds is < 0 or > int.MaxValue / 1000))
            throw new InvalidDataException("持续状态更新超过本版字段边界");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(checked((byte)rows.Count));
        foreach (var row in rows) { writer.Write(row.ItemId); writer.Write(row.RemainingSeconds); }
        return stream.ToArray();
    }

    public static byte[] Grant(int itemId, int seconds)
    {
        byte[] body = new byte[9]; body[0] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), itemId);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5), seconds);
        return body;
    }

    public static byte[] ItemEffect(ushort actor, int itemId)
    {
        byte[] body = new byte[6];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(body, actor);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), itemId);
        return body;
    }

    public static byte[]? Restore(InventorySnapshot inventory, ConsumableStateKind kind, long now)
    {
        var rows = inventory.ConsumableStates.Where(row => row.Kind == kind && row.UntilMilliseconds - now >= 1000).ToArray();
        if (rows.Length == 0) return null;
        if (rows.Length > byte.MaxValue) throw new InvalidDataException("持续状态超过客户端列表容量");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(checked((byte)rows.Length));
        foreach (var row in rows)
        {
            writer.Write(row.ItemId);
            // ARAD174把剩余秒乘1000后交给效果对象，不能让有符号毫秒溢出。
            writer.Write(checked((int)Math.Min((row.UntilMilliseconds - now) / 1000, int.MaxValue / 1000)));
        }
        return stream.ToArray();
    }
}
