namespace DfoServer.Network.Builders;

internal static class RaidPackets
{
    // ACK664 CREATE_RAID：客户端处理器 02343D80（0234A07A 经 028F8C30 注册）。公共 ACK 前缀 success:u8；失败追加 error:u16（[ebp+0x10]）。
    // 失败码 → dstr：3→32059、7→32061、0x13→32060、0x29→32056、0x2E→32057、0x30→32058、0x9F→281、0xA6→72874、0xDD→69004、0xDC→69003、
    // 0x22→75475、1→67067、0xF6→25178、0x75→32129（0xD9/0xF7 带格式参数）；证据 evidence/raid-create-flow-static-20261021.txt。
    // 成功体 u32 团本 id 交 01DDC920 写 [062EF51C]+4；01DC72F0 再用它经 01DC7260 在 +0x4C 记录表里按记录 [+0]（0x24E/0x24F 的 u32 key）查找，
    // 所以成功前必须已有同 key 的团本记录（先发 0x24E）。01DDC920 在旧值已不是 -1 时直接返回，重复设置无副作用。
    // dstr 32060「抱歉， 当前状态下无法入场。」：服务端尚无团本实例时的中立拒绝。
    internal const ushort CreateUnavailable = 0x13;

    public static byte[] CreateFailure(ushort error = CreateUnavailable) => [0, (byte)error, (byte)(error >> 8)];

    public static byte[] CreateSuccess(uint key) => [1, (byte)key, (byte)(key >> 8), (byte)(key >> 16), (byte)(key >> 24)];

    // ACK665 LEAVE_RAID（02344640，0234A08D 注册）：成功读 u8 强退标志（非 0 弹 dstr 46791「已经强制退出攻坚队。」），
    // 之后关窗 0xAC/0xAE，01DD9150 经 01DD6BE0 删除"我的团本"记录并把 [062EF51C]+4 置 -1；不再读任何字段。
    public static readonly byte[] LeaveSuccess = [1, 0];

    // ACK666 START_RAID（023441F0，0234A0B3 注册）：结果≠0 不读字段；结果 0 读 u16 码：0x13→65066「当前状态下， 无法开始攻坚战。」、
    // 8→49075「只有攻坚队长才能开始。」、0x16→49077「攻坚队要求的最低人数不足， 无法开始。」（0x11/0xD9 另带 u32 n + n×u32，不用）。
    // 开战成功后的入场包序未恢复，所以人数足够时也回 0x13，不回成功。
    internal const ushort StartUnavailable = 0x13, StartNotLeader = 8, StartTooFewMembers = 0x16;

    public static byte[] StartFailure(ushort error) => CreateFailure(error);

    // ACK667 SET_RAID_WAITING（023449F0，0234A0A0 注册）：成功不读字段；失败 u16 码：0x13→49080「已经加入了攻坚队。」、
    // 0x12→49081「已经添加到攻坚队的待机列表。」、0xDC→69003 每日、0xDD→69004 每周（次数上限，未接入）。
    internal const ushort WaitingInRaid = 0x13, WaitingAlready = 0x12;
    public static readonly byte[] WaitingSuccess = [1];

    // ACK670 MODIFY_RAID_INFO（0233CF90，注册表 0x29E）：成功不读字段；失败 u16 码：8→65095「只有攻坚队队长可以使用。」、
    // 0x13→32057「抱歉， 不存在该攻坚队。」、0x9F→281「包含无法使用的文字」并重开建队窗（不用）。
    internal const ushort ModifyNotLeader = 8, ModifyNoRaid = 0x13;
    public static readonly byte[] ModifySuccess = [1];

    // NOTI591 op1（023C88F0）：只读key/op，删除招募记录。
    public static byte[] RemoveRecord(uint key) => Build(writer => { writer.Write(key); writer.Write(1u); });

    // NOTI 0x24F RAID_MODIFY op2（023C8937）：u32 key, u32 2, 整条记录（01DD08E0，与 0x24E 记录同形、无尾 u8）；只更新已有记录，不存在即丢弃。
    public static byte[] UpdateRecord(uint key, byte[] title, byte raidType, ushort leader, byte[] leaderName, byte leaderSquad = 0) => Build(writer =>
    {
        writer.Write(key);
        writer.Write(2u);
        WriteRecord(writer, key, title, raidType, leader, leaderName, leaderSquad);
    });

    // ACK658 RAID_ENTRY_COST_INFO（0233C3E0，0234A067 注册）：成功直接返回，不读字段；失败才走 01DC6600。
    public static readonly byte[] EntryCostSuccess = [1];

    // 字符串线格式（03BE25F0）：u32 字节长 + L 字节；L==0 或 >= 上限时客户端不消费字节并用缺省文本（dstr 669「没有名字」）。
    public const int TextLimit = 0x100;

    // 成员块（01DCF570，记录 +0x38 与成员表 +0xC8 每项 0x8C 字节）读序：
    // u16 +0x00 角色号（START_RAID 01DC7Cxx 要求队长块 +0x00 == 0643A52C；0643A52C 由 ACK4 SELECT_CHARACTER 023415C0 第 9 字节 u16 写入，
    //   服务端 TownPackets.Select 写的是 Character.Id）；成员窗口 02177872/0217788A 也只按这个 u16 找角色对象取等级/名字/职业。
    // u8 +0x04；str(0x100) +0x08（缺省 dstr 669「没有名字」，写角色名）；u8 +0x28；u8 +0x2C；u8 +0x2D；u8 +0x2E；u32 +0x30；u8 +0x34；u8 +0x35；
    // u8 +0x38；u32 +0x3C；u16 +0x40；u32 X；u8 丢弃；str(0x11) +0x44；u8 +0x64；u16 +0x66；u32 +0x68；u32 m + m×(u32,u32)；
    // u8 +0x80；u32 +0x84；u8 +0x81；u16 +0x88；u8 +0x8A。除角色号、名字、+0x2D分队外语义未证明，写0（空串L=0、m=0）。
    public static void WriteMember(BinaryWriter writer, ushort character, byte[] name, byte squad = 0)
    {
        writer.Write(character);
        writer.Write((byte)0);
        WriteText(writer, name, TextLimit);
        writer.Write(new byte[] { 0, 0, squad, 0 }); // +0x2D由0216CCBD/021747xx按界面分队比较，其余保持未知零值。
        writer.Write(0u);                    // +0x30
        writer.Write(new byte[3]);           // +0x34 +0x35 +0x38
        writer.Write(0u);                    // +0x3C
        writer.Write((ushort)0);             // +0x40
        writer.Write(0u);                    // X
        writer.Write((byte)0);               // 丢弃
        writer.Write(0u);                    // str(0x11) +0x44，L=0
        writer.Write((byte)0);               // +0x64
        writer.Write((ushort)0);             // +0x66
        writer.Write(0u);                    // +0x68
        writer.Write(0u);                    // m
        writer.Write((byte)0);               // +0x80
        writer.Write(0u);                    // +0x84
        writer.Write((byte)0);               // +0x81
        writer.Write((ushort)0);             // +0x88
        writer.Write((byte)0);               // +0x8A
    }

    // 记录（01DD08E0）：u32 key；str(0x100) 标题 +0x04；u8 +0x24 团本类型（01DDADC0 按 raid.lst 下标取脚本与 PHASE NAME）；
    // u8 +0x28；u8 +0x30 阶段下标（-1 当 0）；u32 +0x34；u8 +0xDC +0xDE +0xDF +0xE0 +0xE1 +0xE2 +0xE3 +0xE5 +0xE4；队长成员块 +0x38。
    // +0x28/+0x34/+0xDC.. 语义未证明，写 0（实机 2026-10-21 23:07 同形记录未崩溃）。
    public static void WriteRecord(BinaryWriter writer, uint key, byte[] title, byte raidType, ushort leader, byte[] leaderName, byte leaderSquad = 0)
    {
        writer.Write(key);
        WriteText(writer, title, TextLimit);
        writer.Write(raidType);
        writer.Write((byte)0);               // +0x28
        writer.Write((byte)0);               // +0x30 阶段 0
        writer.Write(0u);                    // +0x34
        writer.Write(new byte[9]);           // +0xDC +0xDE +0xDF +0xE0 +0xE1 +0xE2 +0xE3 +0xE5 +0xE4
        WriteMember(writer, leader, leaderName, leaderSquad);
    }

    // NOTI 0x24E RAID_LIST（023CAE20）：u32 n；n × { 记录 ; u8 → 记录+0xC4 }；重复 key 不覆盖旧记录。
    // +0xC4 > 1 时 START_RAID 跳过客户端人数检查，语义未证明，写 0。
    public static byte[] RaidList(uint key, byte[] title, byte raidType, ushort leader, byte[] leaderName, byte leaderSquad = 0) => Build(writer =>
    {
        writer.Write(1u);
        WriteRecord(writer, key, title, raidType, leader, leaderName, leaderSquad);
        writer.Write((byte)0);
    });

    // NOTI 0x24F RAID_MODIFY op3（023C897B）：u32 key, u32 3, u8 M, M × 成员块（01DD7420）。key 频道/服务器与当前一致时
    // 01DDC920(key) 设为我的团本并 2E23A40 开窗（实机 23:07 M=0 已开窗），随后 01DD4780(1) 刷新。
    public static byte[] OpenWithMembers(uint key, IReadOnlyList<(ushort Character, byte[] Name)> members) => Build(writer =>
    {
        if (members.Count > byte.MaxValue) throw new InvalidDataException("成员数超过 u8");
        writer.Write(key);
        writer.Write(3u);
        writer.Write((byte)members.Count);
        foreach (var (character, name) in members) WriteMember(writer, character, name);
    });

    public static byte[] OpenWithSquads(uint key, IReadOnlyList<(ushort Character, byte[] Name, byte Squad)> members) => Build(writer =>
    {
        writer.Write(key); writer.Write(3u);
        WriteMembers(writer, members);
    });

    public static byte[] RaidListBatch(IReadOnlyList<(uint Key, byte[] Title, byte Type, ushort Leader, byte[] Name, byte Squad)> rows) => Build(writer =>
    {
        writer.Write((uint)rows.Count);
        foreach (var row in rows)
        {
            WriteRecord(writer, row.Key, row.Title, row.Type, row.Leader, row.Name, row.Squad);
            writer.Write((byte)0);
        }
    });

    // 02349A20成功：mode0读记录，mode1读成员，mode2两者；21失败仍读mode/key。
    public static byte[] Information(byte mode, uint key, byte[] title, byte type, ushort leader, byte[] name,
        IReadOnlyList<(ushort Character, byte[] Name, byte Squad)> members) => Build(writer =>
    {
        if (mode > 2) throw new InvalidDataException("团本查询mode错误");
        writer.Write((byte)1); writer.Write(mode); writer.Write(key);
        if (mode is 0 or 2) WriteRecord(writer, key, title, type, leader, name, members.Single(m => m.Character == leader).Squad);
        if (mode is 1 or 2) WriteMembers(writer, members);
    });

    public static byte[] InformationFailure(byte mode, uint key) => Build(writer =>
    {
        writer.Write((byte)0); writer.Write((ushort)21); writer.Write(mode); writer.Write(key);
    });

    static void WriteMembers(BinaryWriter writer, IReadOnlyList<(ushort Character, byte[] Name, byte Squad)> members)
    {
        writer.Write(checked((byte)members.Count));
        foreach (var member in members) WriteMember(writer, member.Character, member.Name, member.Squad);
    }

    // 023A1070/023A12D0：服务器和频道取本地全局，不在本消息中重复写入。
    public static byte[] WaitingList(IReadOnlyList<(ushort Character, byte Type)> members) => Build(writer =>
    {
        writer.Write(checked((ushort)members.Count));
        foreach (var member in members) { writer.Write(member.Character); writer.Write(member.Type); }
    });

    public static byte[] WaitingChanged(ushort character, byte type, bool added) => Build(writer =>
    {
        writer.Write(added ? (byte)0 : (byte)1); writer.Write(character);
        if (added) writer.Write(type);
    });

    // 非PVP场景的mode10：023D1B03读三个u16；NOTI8只消费共同7B前缀。
    public static byte[] Peer(ushort character, uint peer, bool invite) => Build(writer =>
    {
        writer.Write(character); writer.Write((byte)10); writer.Write(peer);
        if (invite) { writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); }
    });

    static void WriteText(BinaryWriter writer, byte[] text, int limit)
    {
        if (text.Length >= limit) throw new InvalidDataException("团本字符串超过客户端上限");
        writer.Write((uint)text.Length);
        writer.Write(text);
    }

    static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream)) write(writer);
        return stream.ToArray();
    }

    public readonly record struct WeeklyClear(byte RaidType, uint DailyUsed, uint WeeklyUsed);

    // NOTI 0x5A6 RAID_WEEKLY_CLEAR_INFO（ARAD 01DD61C0）：先清空团本管理器 [062EF51C]+0x234 表，再读
    //   u8 n；n × { u8 团本类型, u32 a, u32 b, u8 m；m × { u8, u32, u32 } }，按类型写入 {a, b, 子表}。
    // 01DCBE90(type, 0/1) 取 a/b（缺项返回 -1）；01711690 显示每日 (1-a)/1、每周 (WEEKLY CLEAR COUNT-b)/WEEKLY CLEAR COUNT，
    // 所以 a/b 是已用次数。缺项时 -1 显示成 2/1 与 3/2。子表 m 的含义未证明，固定写 0。
    public static byte[] WeeklyClearInfo(IReadOnlyList<WeeklyClear> rows)
    {
        if (rows.Count > byte.MaxValue || rows.Select(row => row.RaidType).Distinct().Count() != rows.Count)
            throw new InvalidDataException("团本周通关表行数或类型无效");
        var body = new byte[1 + rows.Count * 10];
        body[0] = (byte)rows.Count;
        for (int index = 0; index < rows.Count; index++)
        {
            int offset = 1 + index * 10;
            body[offset] = rows[index].RaidType;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset + 1), rows[index].DailyUsed);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset + 5), rows[index].WeeklyUsed);
            body[offset + 9] = 0;
        }
        return body;
    }
}
