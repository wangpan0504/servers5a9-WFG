using System.Buffers.Binary;
using DfoServer.Game.Raids;

namespace DfoServer.Network.Builders;

internal static class RaidCombatPackets
{
    // 02396E0E/1D：u8状态、i8阶段。只允许本模块已实现的阶段状态。
    public static byte[] State(AntonRaidBattleView view) => [(byte)view.State, checked((byte)view.Phase)];

    // 023B2A54..2AAA，KB-20261026T063752-4691836ed4。未知列必须显式给定，不生成评分或倍率。
    public static byte[] Result(RaidResultDisplay result)
    {
        byte[] body = new byte[10];
        body[0] = result.Result; body[1] = result.Phase;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), result.Seconds);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), result.RawValue);
        body[8] = result.Rank; body[9] = result.RawFlag;
        return body;
    }

    // 安徒恩专用01DC8B70；不是023CA1B0的普通团本布局。660成功ACK另发单字节01。
    public static byte[] Reward(byte kind, IReadOnlyList<RaidRewardDisplayRow> rows)
    {
        int size = checked(2 + rows.Sum(row => checked(4 + 8 * row.Items.Length)));
        byte[] body = new byte[size]; int at = 0;
        body[at++] = kind; body[at++] = checked((byte)rows.Count);
        foreach (var row in rows)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at), row.RawOwner); at += 2;
            body[at++] = row.Card; body[at++] = checked((byte)row.Items.Length);
            foreach (var item in row.Items)
            {
                body[at++] = item.RawCode;
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at), item.ItemId); at += 4;
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at), item.DisplayValue); at += 2;
                body[at++] = item.RawFlag;
            }
        }
        return body;
    }
}
