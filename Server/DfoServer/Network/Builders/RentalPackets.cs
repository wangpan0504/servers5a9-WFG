using System.Buffers.Binary;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders;

internal static class RentalPackets
{
    public static byte[] ChargeSuccess(uint type, int points)
    {
        byte[] body = new byte[13];
        body[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), type);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), checked((uint)Math.Max(points, 0)));
        return body;
    }

    public static byte[] ChargeFailure(byte code = 1) => [0, code];
    public static byte[] RentSuccess() => [1, 0, 0, 0, 0];
    public static byte[] RentFailure(byte code = 1) => [0, code, 0, 0, 0];

    // S2C988 使用账号虚拟余额与剩余秒；持久化保留绝对截止，幸运星没有实体物品行。
    public static byte[] State(InventorySnapshot inventory, DateTimeOffset now)
    {
        long utc = now.ToUnixTimeSeconds();
        var active = inventory.Items.Where(item => item.Core.IsRental && item.Core.Kind == DfoServer.GameWorld.Items.ItemKind.Equipment &&
                !item.IsExpired(utc))
            .GroupBy(item => item.Core.ItemId)
            .Select(group => group.OrderByDescending(item => item.ExpireTime).First())
            .OrderBy(item => item.Core.ItemId)
            .ToArray();
        byte[] body = new byte[8 + active.Length * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(body, checked((uint)inventory.AccountWallet.RentalPoints));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), checked((uint)active.Length));
        for (int i = 0; i < active.Length; i++)
        {
            int offset = 8 + i * 8;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), checked((uint)active[i].Core.ItemId));
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset + 4), checked((uint)active[i].RemainingSeconds(utc)));
        }
        return body;
    }
}
