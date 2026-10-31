using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Inventory;

internal sealed record ConsumableDisguiseRequest(byte Type, int Seconds, int ItemId);

internal static class ConsumableDisguiseRequests
{
    public static ConsumableDisguiseRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 9) throw new InvalidDataException("Disguise request must contain 9 bytes");
        return new(body[0], BinaryPrimitives.ReadInt32LittleEndian(body[1..]), BinaryPrimitives.ReadInt32LittleEndian(body[5..]));
    }

    public static void Cancel(ReadOnlySpan<byte> body)
    {
        if (!body.IsEmpty) throw new InvalidDataException("Disguise cancel must be empty");
    }
}
