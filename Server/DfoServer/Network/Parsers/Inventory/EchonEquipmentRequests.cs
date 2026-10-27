using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class EchonEquipmentRequests
{
    public static short Upgrade(ReadOnlySpan<byte> body)
    {
        if (body.Length != 2 || BinaryPrimitives.ReadInt16LittleEndian(body) < 0) throw Invalid();
        return BinaryPrimitives.ReadInt16LittleEndian(body);
    }
    public static EchonOptionRequest Option(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4 || BinaryPrimitives.ReadInt16LittleEndian(body) < 0 || body[2] > 1 || body[3] == 0) throw Invalid();
        return new(BinaryPrimitives.ReadInt16LittleEndian(body), body[2], body[3]);
    }
    public static EchonSetRequest Set(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2 || body[0] is < 1 or > 4 || body.Length <= body[0] + 1) throw Invalid();
        int count = body[0], parts = body[count + 1];
        if (parts is < 3 or > 6 || body.Length != count + 2 + parts * 2) throw Invalid();
        var options = body.Slice(1, count).ToArray();
        var slots = new short[parts];
        for (int at = 0; at < parts; at++) slots[at] = BinaryPrimitives.ReadInt16LittleEndian(body[(count + 2 + at * 2)..]);
        if (options.Contains((byte)0) || options.Distinct().Count() != count || slots.Any(slot => slot < 0) || slots.Distinct().Count() != parts)
            throw Invalid();
        return new(options, slots);
    }
    static InvalidDataException Invalid() => new("艾肯装备请求字段或长度无效");
}
