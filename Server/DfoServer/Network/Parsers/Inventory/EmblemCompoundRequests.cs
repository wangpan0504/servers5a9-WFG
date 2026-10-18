using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class EmblemCompoundRequests
{
    const int InputSize = 6;

    public static EmblemCompoundRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty || body[0] is < 2 or > 5 || body.Length != 1 + body[0] * InputSize)
            throw new InvalidDataException("徽章合成请求正文长度或材料数无效");
        var inputs = ImmutableArray.CreateBuilder<EmblemCompoundInput>(body[0]);
        for (int index = 0; index < body[0]; index++)
        {
            int at = 1 + index * InputSize;
            uint itemId = BinaryPrimitives.ReadUInt32LittleEndian(body[at..]);
            ushort slot = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 4)..]);
            if (itemId is 0 or > int.MaxValue || slot > 351)
                throw new InvalidDataException("徽章合成材料编号或槽位无效");
            inputs.Add(new(checked((int)itemId), checked((short)slot)));
        }
        return new(inputs.MoveToImmutable());
    }
}
