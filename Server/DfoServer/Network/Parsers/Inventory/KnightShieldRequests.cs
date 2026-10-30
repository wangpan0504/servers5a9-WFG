using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class KnightShieldRequests
{
    public static KnightShieldDeck Change(ReadOnlySpan<byte> body)
    {
        if (body.Length != 20) throw new InvalidDataException("CMD657盾牌配置必须为五个i32");
        int[] values = new int[KnightShieldDeck.Count];
        for (int index = 0; index < values.Length; index++)
            values[index] = BinaryPrimitives.ReadInt32LittleEndian(body[(index * 4)..]);
        var result = new KnightShieldDeck([.. values]);
        result.Validate();
        return result;
    }
}
