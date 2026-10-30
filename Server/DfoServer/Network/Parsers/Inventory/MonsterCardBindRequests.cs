using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class MonsterCardBindRequests
{
    public static MonsterCardBindRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length < 3)
            throw new InvalidDataException("怪物卡片合成请求过短");
        ushort synthesizer = BinaryPrimitives.ReadUInt16LittleEndian(body);
        byte count = body[2];
        if (count is < 1 or > 5 || body.Length != 3 + count * 2)
            throw new InvalidDataException("怪物卡片合成请求正文长度或卡片数无效");
        if (synthesizer > 351)
            throw new InvalidDataException("怪物卡片合成器槽位无效");
        var slots = ImmutableArray.CreateBuilder<short>(count);
        for (int index = 0; index < count; index++)
        {
            ushort slot = BinaryPrimitives.ReadUInt16LittleEndian(body[(3 + index * 2)..]);
            if (slot > 351 || slot == synthesizer)
                throw new InvalidDataException("怪物卡片合成卡片槽位无效");
            slots.Add(checked((short)slot));
        }
        return new(checked((short)synthesizer), slots.MoveToImmutable());
    }
}
