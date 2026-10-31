using System.Buffers.Binary;
using DfoServer.Game.Raids;

namespace DfoServer.Network.Builders;

internal static class RaidCombatPackets
{
    // 015BCB50：op1无尾部，op5只读真实副本编号。
    internal static byte[] PreyStatus(int operation, int? dungeon = null)
    {
        if (operation == 1 && dungeon is null) return [1, 0, 0, 0];
        if (operation != 5 || dungeon is not > 0) throw new ArgumentOutOfRangeException(nameof(operation));
        byte[] body = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(body, operation);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), dungeon.Value);
        return body;
    }
    // Luke专用准备层：02386120按1/4/4/4读取，01DD06DE忽略中间列并保存now+seconds。
    // 零秒不是RESET，kind2 UI存在除零风险；本方法未接入正式投影。
    public static byte[] LukePositiveTimer(sbyte kind, int dungeon, uint ignoredRaw, uint seconds)
    {
        if (kind is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (dungeon <= 0) throw new ArgumentOutOfRangeException(nameof(dungeon));
        if (ignoredRaw != 0) throw new ArgumentOutOfRangeException(nameof(ignoredRaw));
        if (seconds is 0 or > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
        byte[] body = new byte[13];
        body[0] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), checked((uint)dungeon));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), ignoredRaw);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), seconds);
        return body;
    }

    // 0239ED80只读u8已使用次数；02617674按phase资源上限判断，不是剩余数量。
    public static byte[] PhaseCoins(byte used) => used <= LukeRaidRevivalPolicy.PhaseLimit
        ? [used] : throw new ArgumentOutOfRangeException(nameof(used));

    // 597首字节为计时槽；本批只使用已证攻略标题的key0，不推导待命状态。
    public static byte[] PhaseRemaining(uint seconds)
    {
        if (seconds > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
        byte[] body = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), seconds);
        return body;
    }

    public static byte[] AntonWaitTimer(uint seconds, uint endUnixSeconds)
    {
        byte[] body = new byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), endUnixSeconds);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), seconds);
        return body;
    }

    // 频道23/25的01DD076A保存(end-seconds,end)，不使用Luke的now+seconds分支。
    public static byte[] AntonDungeonTimer(int kind, int dungeon, uint seconds, uint end)
    {
        if (kind is < 1 or > 3 || dungeon <= 0 || seconds is 0 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        byte[] body = AntonWaitTimer(seconds, end);
        body[0] = checked((byte)kind);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), dungeon);
        return body;
    }

    public static byte[] AntonWaitRemaining(byte slot, uint seconds)
    {
        byte[] body = PhaseRemaining(seconds); body[0] = slot; return body;
    }

    // 02396E0E/1D：u8状态、i8阶段。只允许本模块已实现的阶段状态。
    public static byte[] State(AntonRaidBattleView view) => [(byte)view.State, checked((byte)view.Phase)];

    // 0237F2C0完整读序为6+5N；首字节仅消费，尾字段是感染地下城索引，均由调用方显式给出。
    public static byte[] Dungeons(byte reserved, IReadOnlyList<(int Id, DfoServer.GameWorld.Raids.RaidDungeonState State)> rows,
        int infectionDungeon)
    {
        if (rows.Count > byte.MaxValue || rows.Any(row => row.Id <= 0 || !Enum.IsDefined(row.State)) ||
            rows.Select(row => row.Id).Distinct().Count() != rows.Count || infectionDungeon < 0)
            throw new InvalidDataException("攻坚地下城快照非法");
        byte[] body = new byte[checked(6 + rows.Count * 5)];
        body[0] = reserved; body[1] = (byte)rows.Count;
        for (int index = 0; index < rows.Count; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2 + index * 5), rows[index].Id);
            body[6 + index * 5] = (byte)rows[index].State;
        }
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2 + rows.Count * 5), infectionDungeon);
        return body;
    }

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
