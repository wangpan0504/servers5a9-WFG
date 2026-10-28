using System.Buffers.Binary;

namespace DfoServer.Network.Builders;

internal static class TradePackets
{
    public const byte TradeSpace = 4;
    public const uint TradeOfferMarker = 0x3b9ac9fe;

    public static byte[] Invite(ushort inviter, uint token, uint unixNow)
    {
        var body = new byte[11];
        BinaryPrimitives.WriteUInt16LittleEndian(body, inviter);
        body[2] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3), token);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), unixNow);
        return body;
    }

    public static byte[] Accepted(ushort invitee, uint token, uint unixNow)
    {
        var body = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(body, invitee);
        body[2] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3), token);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), unixNow);
        return body;
    }

    public static byte[] ResponseAck(ushort inviter) => [1, (byte)inviter, (byte)(inviter >> 8), 1, 0];
    public static byte[] State(ushort characterId, byte state) => [(byte)characterId, (byte)(characterId >> 8), state];
    public static byte[] StateAck() => [1];
    public static byte[] RegisterFinished() => [];

    public static byte[] ChangedItem(ushort tradeSlot, uint templateId, uint marker, ushort durability)
    {
        var body = new byte[123];
        BinaryPrimitives.WriteUInt16LittleEndian(body, tradeSlot);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), templateId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(6), marker);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(10), durability);
        return body;
    }

    public static byte[] ChangedGold(uint gold) => ChangedItem(0, 0, gold, 0);
    public static byte[] ClearedRow(ushort tradeSlot) => ChangedItem(tradeSlot, uint.MaxValue, 0, 0);

    public static byte[] MoveAck(byte source, ushort sourceSlot, uint value, byte destination, ushort destinationSlot)
    {
        var body = new byte[11];
        body[0] = 1;
        body[1] = source;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), sourceSlot);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), value);
        body[8] = destination;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(9), destinationSlot);
        return body;
    }

    public static byte[] Finish(IReadOnlyList<(ushort TradeSlot, ushort DestinationSlot)> rows)
    {
        var body = new byte[checked(2 + rows.Count * 4)];
        BinaryPrimitives.WriteUInt16LittleEndian(body, checked((ushort)rows.Count));
        for (int index = 0; index < rows.Count; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2 + index * 4), rows[index].TradeSlot);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4 + index * 4), rows[index].DestinationSlot);
        }
        return body;
    }

    public static byte[] Close() => [0, 0];
}
