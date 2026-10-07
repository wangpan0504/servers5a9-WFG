using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct RentalChargeRequest(uint Type, int Count)
{
    public static RentalChargeRequest Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length != 21)
            throw new InvalidDataException("租赁点购买请求长度必须为 21");
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(body[13..17]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(body[17..21]);
        if (type != 1 || count == 0 || count > int.MaxValue)
            throw new InvalidDataException("租赁点购买参数无效");
        return new(type, checked((int)count));
    }
}

internal readonly record struct RentalEquipmentRequest(uint ItemId, uint PackedJobTier)
{
    public static RentalEquipmentRequest Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length != 21)
            throw new InvalidDataException("装备租赁请求长度必须为 21");
        uint itemId = BinaryPrimitives.ReadUInt32LittleEndian(body[13..17]);
        if (itemId == 0)
            throw new InvalidDataException("租赁物品 ID 不能为零");
        return new(itemId, BinaryPrimitives.ReadUInt32LittleEndian(body[17..21]));
    }

    public ushort Job => checked((ushort)(PackedJobTier & 0xffff));
    public ushort Tier => checked((ushort)(PackedJobTier >> 16));
}

internal static class RentalRequests
{
    public static RentalChargeRequest Charge(ReadOnlySpan<byte> body) => RentalChargeRequest.Parse(body);
    public static RentalEquipmentRequest Equipment(ReadOnlySpan<byte> body) => RentalEquipmentRequest.Parse(body);
}
