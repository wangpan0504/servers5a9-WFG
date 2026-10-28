using System.Buffers.Binary;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers;

internal static class GuildCargoRequests
{
    public static bool TryMove(ReadOnlySpan<byte> body, out GuildCargoRequest request)
    {
        request = null!;
        if (body.Length != 12 || BinaryPrimitives.ReadUInt16LittleEndian(body) > short.MaxValue ||
            BinaryPrimitives.ReadUInt16LittleEndian(body[6..]) > short.MaxValue ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[2..]) is 0 or > int.MaxValue ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[8..]) > int.MaxValue) return false;
        request = new(false, InventoryListType.Main, 0, BinaryPrimitives.ReadInt16LittleEndian(body),
            BinaryPrimitives.ReadInt32LittleEndian(body[2..]), 0)
        {
            Move = true, TargetSlot = BinaryPrimitives.ReadInt16LittleEndian(body[6..]),
            TargetItemId = BinaryPrimitives.ReadInt32LittleEndian(body[8..])
        };
        return true;
    }

    public static bool TryDeposit(ReadOnlySpan<byte> body, out GuildCargoRequest request)
    {
        request = null!;
        if (body.Length != 13 || body[0] is not (0 or 7) ||
            BinaryPrimitives.ReadUInt16LittleEndian(body[1..]) > short.MaxValue ||
            BinaryPrimitives.ReadUInt16LittleEndian(body[11..]) > short.MaxValue ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[3..]) is 0 or > int.MaxValue ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[7..]) is 0 or > int.MaxValue) return false;
        request = new(true, (InventoryListType)body[0], BinaryPrimitives.ReadInt16LittleEndian(body[1..]),
            BinaryPrimitives.ReadInt16LittleEndian(body[11..]), BinaryPrimitives.ReadInt32LittleEndian(body[3..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[7..]));
        return true;
    }

    public static bool TryWithdraw(ReadOnlySpan<byte> body, out GuildCargoRequest request)
    {
        request = null!;
        if (body.Length != 11 || body[10] is not (0 or 7) ||
            BinaryPrimitives.ReadUInt16LittleEndian(body) > short.MaxValue ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[2..]) is 0 or > int.MaxValue ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[6..]) is 0 or > int.MaxValue) return false;
        request = new(false, (InventoryListType)body[10], 0, BinaryPrimitives.ReadInt16LittleEndian(body),
            BinaryPrimitives.ReadInt32LittleEndian(body[2..]), BinaryPrimitives.ReadInt32LittleEndian(body[6..]));
        return true;
    }
}
