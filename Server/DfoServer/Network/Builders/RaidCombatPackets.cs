using System.Buffers.Binary;
using DfoServer.Game.Raids;

namespace DfoServer.Network.Builders;

internal static class RaidCombatPackets
{
    // 02396E0E/1D：u8状态、i8阶段。只允许本模块已实现的阶段状态。
    public static byte[] State(AntonRaidBattleView view) => [(byte)view.State, checked((byte)view.Phase)];

    // 0237F210：使用原.symbol显式ID；不发(0,0)清表哨兵，避免清其它共享符号。
    public static byte[] Symbols(IReadOnlyList<(int Id, int Value)> rows)
    {
        if (rows.Count > byte.MaxValue || rows.Any(row => row.Id <= 0) || rows.Select(row => row.Id).Distinct().Count() != rows.Count)
            throw new InvalidDataException("攻坚符号快照越界或含清表哨兵");
        byte[] body = new byte[checked(1 + rows.Count * 8)];
        body[0] = checked((byte)rows.Count);
        for (int index = 0; index < rows.Count; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1 + index * 8), rows[index].Id);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5 + index * 8), rows[index].Value);
        }
        return body;
    }

    // 02380E70：中间字节完整读取但不消费；保留参数，不命名为phase。
    public static byte[] DungeonState(int dungeon, byte reserved, DfoServer.GameWorld.Raids.RaidDungeonState state)
    {
        if (dungeon <= 0 || !Enum.IsDefined(state)) throw new InvalidDataException("攻坚地下城状态非法");
        byte[] body = new byte[6];
        BinaryPrimitives.WriteInt32LittleEndian(body, dungeon);
        body[4] = reserved; body[5] = (byte)state;
        return body;
    }

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
