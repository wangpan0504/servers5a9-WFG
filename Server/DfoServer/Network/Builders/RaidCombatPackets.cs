using System.Buffers.Binary;
using DfoServer.Game.Raids;

namespace DfoServer.Network.Builders;

internal static class RaidCombatPackets
{
    // 015F8790：i8阶段/图数，u16图号/u8布局/i8牌数，逐牌u32模板/u8保留，尾部为自动展示标记。
    internal static byte[] LukeCards(LukeCardsDisplay display)
    {
        if (display.Phase > 1 || display.Dungeons.Length > sbyte.MaxValue ||
            display.Dungeons.Any(row => row.Monsters.Length > sbyte.MaxValue))
            throw new InvalidDataException("卢克牌组超出客户端范围");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(display.Phase); writer.Write(checked((byte)display.Dungeons.Length));
        foreach (var row in display.Dungeons)
        {
            writer.Write(row.Dungeon); writer.Write(row.Pattern); writer.Write(checked((byte)row.Monsters.Length));
            foreach (int monster in row.Monsters) { writer.Write(checked((uint)monster)); writer.Write((byte)0); }
        }
        // 03B94791..C6仅在卢克城镇自动展示未读牌组；读取后客户端自行清除此标记。
        writer.Write(display.AutoDisplay ? (byte)1 : (byte)0);
        return stream.ToArray();
    }

    internal static byte[] LukeNamed(int monster, bool killed)
    {
        byte[] body = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(body, monster); body[4] = killed ? (byte)1 : (byte)0;
        return body;
    }

    // 02389A10：逐行u32地下城/u8操作/u8人数/u16角色；0移除，2静默登记，不重播通关横幅。
    internal static byte[] Participation(IReadOnlyList<RaidRunDisplay> previous, IReadOnlyList<RaidRunDisplay> current,
        bool notifyClears = false)
    {
        // op4不修改参与位置，只报告本次Run已经提交的通关；不能从DGN最终Hide/Close反推。
        var cleared = notifyClears ? current.Where(row => row.Cleared &&
            !previous.Any(old => old.Run == row.Run && old.Cleared)).ToArray() : [];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(checked((byte)(previous.Count + current.Count + cleared.Length)));
        Write(previous, 0);
        Write(current, 2);
        Write(cleared, 4);
        return stream.ToArray();
        void Write(IReadOnlyList<RaidRunDisplay> rows, byte operation)
        {
            foreach (var row in rows)
            {
                // 通关成员在事实提交时冻结，不能随逐人退场缩成最后一人。
                var members = operation == 4 ? row.ClearedMembers : row.Members;
                writer.Write(row.Dungeon); writer.Write(operation); writer.Write(checked((byte)members.Length));
                foreach (ushort member in members) writer.Write(member);
            }
        }
    }

    // 584单行已提交事件，不夹带旧参与位置。
    internal static byte[] RunCleared(uint dungeon, IReadOnlyList<ushort> members)
    {
        if (members.Count is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(members));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); writer.Write(dungeon); writer.Write((byte)4); writer.Write(checked((byte)members.Count));
        foreach (ushort member in members) writer.Write(member);
        return stream.ToArray();
    }

    internal static byte[] StatusHp(IReadOnlyList<RaidRunDisplay> rows)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(checked((byte)rows.Count));
        foreach (var row in rows)
        {
            writer.Write((ushort)row.Squad); writer.Write(checked((byte)row.Members.Length));
            foreach (ushort member in row.Members) writer.Write(member);
            writer.Write(row.UsedCoins); writer.Write(row.Context); writer.Write(checked((byte)row.Values.Length));
            foreach (uint value in row.Values) writer.Write(value);
        }
        return stream.ToArray();
    }

    // 02397850：outer、分队号、登记成员、已用币、695首u32、同序百分比。图集帧1..20直接画分队号。
    internal static byte[] AntonHp(DfoServer.Game.Dungeons.AntonHpDisplay display)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); writer.Write(display.Squad); writer.Write(checked((byte)display.Members.Length));
        foreach (ushort member in display.Members) writer.Write(member);
        writer.Write(display.UsedCoins); writer.Write(display.Context); writer.Write(checked((byte)display.Values.Length));
        foreach (uint value in display.Values) writer.Write(value);
        return stream.ToArray();
    }

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
    // 零秒不是RESET，kind2 UI存在除零风险；取消计时使用独立的已过期区间。
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

    // 01DD06B0按32位加法保存now+末列；FFFFFFFF使end=now-1。
    // 01DD0810先拒绝end<now，既不等待旧计时结束，也不进入零长度比例计算。
    internal static byte[] LukeExpiredTimer(sbyte kind, int dungeon)
    {
        byte[] body = LukePositiveTimer(kind, dungeon, 0, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), uint.MaxValue);
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

    // 单人/引导安徒恩的01DC8B70分支；普通20人安徒恩走AntonRewardPackets的023CA1B0公共16B门控。660成功ACK另发单字节01。
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
