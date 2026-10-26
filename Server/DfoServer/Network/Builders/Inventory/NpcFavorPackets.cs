using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Network.Builders.Inventory;

internal static class NpcFavorPackets
{
    // NOTI746 023A07B0：先清旧视图；u8行数，每行i32 NPC、i32绝对点数、u8赠礼次数、u8奖励标记数。
    public static byte[] Snapshot(ImmutableArray<NpcFavorPoint> rows, DateTimeOffset? utcNow = null)
    {
        NpcFavorPoint.Validate(rows);
        var body = new byte[1 + rows.Sum(row => 10 + (row.Colors == 0 ? 0 : 8))];
        body[0] = checked((byte)rows.Length);
        int at = 1;
        int day = ShopPurchaseCount.Today(utcNow ?? DateTimeOffset.UtcNow);
        foreach (var row in rows)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at), row.NpcId);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at + 4), row.Points);
            body[at + 8] = row.EffectiveGifts(day);
            body[at + 9] = row.Colors == 0 ? (byte)0 : (byte)4;
            at += 10;
            if (row.Colors != 0)
                for (byte index = 0; index < 4; index++)
                {
                    body[at++] = index;
                    body[at++] = row.Color(index);
                }
        }
        return body;
    }

    // ACK814 02369010：赠礼结果的u16为本次实际增量，随后是绝对点数及已赠次数。
    public static byte[] Gift(NpcFavorRequest request, NpcFavorPoint row, int gain)
    {
        var body = new byte[13];
        body[0] = 1;
        body[1] = request.Operation;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), request.NpcId);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), checked((ushort)gain));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), row.Points);
        body[12] = row.GiftCount;
        return body;
    }

    public static byte[] Color(NpcFavorRequest request)
    {
        var body = new byte[7 + request.Colors.Length * 2];
        body[0] = 1;
        body[1] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), request.NpcId);
        body[6] = checked((byte)request.Colors.Length);
        int at = 7;
        foreach (var color in request.Colors)
        {
            body[at++] = color.Index;
            body[at++] = color.Value;
        }
        return body;
    }

    public static byte[] Rejected(ushort error) => [0, (byte)error, (byte)(error >> 8)];
}
