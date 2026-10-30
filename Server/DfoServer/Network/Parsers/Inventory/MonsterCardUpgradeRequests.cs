using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class MonsterCardUpgradeRequests
{
    public static MonsterCardUpgradeRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4 || body[0] != 0 || body[3] is < 1 or > 4 || body.Length != 4 + body[3] * 3)
            throw new InvalidDataException("卡片升级请求容器、数量或长度无效");
        ushort target = BinaryPrimitives.ReadUInt16LittleEndian(body[1..]);
        if (target > 351)
            throw new InvalidDataException("卡片升级目标槽无效");
        var seen = new HashSet<ushort> { target };
        var materials = ImmutableArray.CreateBuilder<short>(body[3]);
        for (int at = 4; at < body.Length; at += 3)
        {
            ushort slot = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 1)..]);
            if (body[at] != 0 || slot > 351 || !seen.Add(slot))
                throw new InvalidDataException("卡片升级材料容器、槽位或重复项无效");
            materials.Add(checked((short)slot));
        }
        return new(checked((short)target), materials.MoveToImmutable());
    }
}
