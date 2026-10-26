using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Network.Parsers;

internal static class NpcFavorRequests
{
    public static NpcFavorRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length < 6 || BinaryPrimitives.ReadUInt32LittleEndian(body[1..]) is 0 or > int.MaxValue)
            throw new InvalidDataException("NPC好感度请求缺少有效目标");
        int id = BinaryPrimitives.ReadInt32LittleEndian(body[1..]);
        if (body[0] is 0 or 2 && body.Length == 8)
            return new(body[0], id, body[5], BinaryPrimitives.ReadUInt16LittleEndian(body[6..]));
        if (body[0] == 1 && body[5] is >= 1 and <= 4 && body.Length == 6 + body[5] * 2)
        {
            var colors = ImmutableArray.CreateBuilder<NpcFavorColor>(body[5]);
            for (int at = 6; at < body.Length; at += 2) colors.Add(new(body[at], body[at + 1]));
            if (colors.Any(color => color.Index > 3) || colors.Select(color => color.Index).Distinct().Count() != colors.Count)
                throw new InvalidDataException("NPC染色通道重复或越界");
            return new(1, id, Colors: colors.ToImmutable());
        }
        throw new InvalidDataException("NPC好感度操作或包体宽度无效");
    }
}
