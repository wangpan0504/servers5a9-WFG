using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class NpcFavorPackets
{
    // NOTI746 023A07B0：先清旧视图；u8行数，每行i32 NPC、i32绝对点数、u8赠礼次数、u8奖励标记数。
    public static byte[] Snapshot(ImmutableArray<NpcFavorPoint> rows)
    {
        NpcFavorPoint.Validate(rows);
        var body = new byte[1 + rows.Length * 10];
        body[0] = checked((byte)rows.Length);
        for (int index = 0; index < rows.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1 + index * 10), rows[index].NpcId);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5 + index * 10), rows[index].Points);
        }
        return body;
    }
}
