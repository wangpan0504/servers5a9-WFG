using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Characters;

namespace DfoServer.Network.Parsers;

// C2S664 CREATE_RAID（客户端 7560188d，发送点 0216E156..0216E1E8，经 03BDF610 起包、03BE0FF0 发出）：
// u8  团本类型：01DC69B0 取当前频道类型减 0x17 查 01DC6A18 表（0x17/0x19→0，0x20→1，0x2D→2，0x32→3，0x3E→4，0x3F→5，其余→6）；
//     魔界大战频道 0x36 落默认值 6，与实机 06 一致。
// u32 字节数 + 队名（03BDF8E0）；随后依次 u8 窗口[+0x210]、u8 [+0x211]、u8 [+0x214]、u8 标志（局部 bl，0216E0BD 可清 bit2）。
// 尾标志低三位分别为输出/协同/辅助招募对象，0216E330由记录+E4回填；三个窗口字段独立保留。
internal readonly record struct CreateRaidRequest(byte RaidType, string Name, byte Field210, byte Field211, byte Field214, byte Flags);
internal readonly record struct RaidSearchRequest(uint Offset, byte RaidType, string Name, bool Closed = false);

// C2S670 MODIFY_RAID_INFO（发送点 0216F165..0216F1B7）：u8 团本类型（01DC69B0）、u32 字节数 + 队名（03BDF8E0）、u8 招募对象掩码。
internal readonly record struct ModifyRaidRequest(byte RaidType, string Name, byte Field);
internal readonly record struct RaidManagerRequest(uint Operation, uint Character, uint Argument);
internal readonly record struct PreyMarkerRequest(uint Operation, uint Group, uint Target);
internal readonly record struct RaidInfoRequest(byte Mode, uint Key);
internal readonly record struct RaidPeerRequest(ushort Character, uint Peer);
internal readonly record struct RaidMonsterHpRequest(uint RawValue, uint[] RawEntries);
internal readonly record struct RaidRewardCardRequest(byte Kind, byte Card);
internal readonly record struct RaidUserRequest(byte Server, ushort Character, byte Channel);
internal readonly record struct RaidOtherUserRequest(byte Server, ushort Character, byte Mode, byte Channel,
    byte RawView = 0, uint RequestId = 0);

internal static partial class RaidRequests
{
    // 01C04D43..01C04D9E：曾用名窗口跨频道分支写server/character/mode1/channel。
    public static RaidOtherUserRequest OtherUser(ReadOnlySpan<byte> body)
    {
        if (body.Length < 5) throw new InvalidDataException("RAID_OTHER_CHANNEL_USERINFO缺少角色定位与模式");
        // 原生mode3丢失页面，配套补丁在原5B后补页面及非零请求号；其他模式仍严格5B。
        if (body[3] == 3)
        {
            if (body.Length != 10 || body[5] is < 1 or > 5 || BinaryPrimitives.ReadUInt32LittleEndian(body[6..]) == 0)
                throw new InvalidDataException("跨频道决斗查询需要完整页面和请求号");
            return new(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[1..]), 3, body[4], body[5],
                BinaryPrimitives.ReadUInt32LittleEndian(body[6..]));
        }
        if (body.Length != 5) throw new InvalidDataException("跨频道资料模式不接受附加字段");
        return new(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[1..]), body[3], body[4]);
    }

    // 01DC6A70依次写目标server:u8、character:u16、channel:u8。
    public static RaidUserRequest CheckUser(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4) throw new InvalidDataException("RAID_CHECK_RAID_USER需要4字节角色定位");
        return new(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[1..]), body[3]);
    }

    // 01DC6920仅报告本人的活动计时状态，不是准备开战按钮或全员准备门禁。
    public static bool MemberState(ReadOnlySpan<byte> body)
    {
        if (body.Length != 1 || body[0] > 1) throw new InvalidDataException("RAID_MEMBER_CHANGE_STATE需要单个布尔值");
        return body[0] == 1;
    }

    // 副本协议：01101417..01101446连续写两个u8；只固定读宽，不猜牌种/槽位全集。
    public static RaidRewardCardRequest RewardCard(ReadOnlySpan<byte> body)
    {
        if (body.Length != 2) throw new InvalidDataException("SELECT_RAID_REWARD_CARD 需要两个u8");
        return new(body[0], body[1]);
    }

    // 现场218终末后的Last动作；不接受其它symbol、操作或减量。
    public static bool AntonEnergyClear(ReadOnlySpan<byte> body) => body.Length == 9 &&
        BinaryPrimitives.ReadUInt32LittleEndian(body) == 110 &&
        BinaryPrimitives.ReadUInt32LittleEndian(body[4..]) == 3000 && body[8] == 2;

    // Transmission_S_Last.act的显式110[-]100；不把其它对象/操作/减量解释为电池Last。
    internal static bool AntonBatteryLast(ReadOnlySpan<byte> body) => body.Length == 9 &&
        BinaryPrimitives.ReadUInt32LittleEndian(body) == 110 &&
        BinaryPrimitives.ReadUInt32LittleEndian(body[4..]) == 100 && body[8] == 2;

    // ARAD 01DC6180: symbol:u32, value:u32, operation:u8。这里只开放三设施+1白名单。
    public static int? LukeFacilitySymbol(ReadOnlySpan<byte> body)
    {
        if (body.Length != 9 || BinaryPrimitives.ReadUInt32LittleEndian(body[4..]) != 1 || body[8] != 1) return null;
        uint symbol = BinaryPrimitives.ReadUInt32LittleEndian(body);
        return symbol is 117 or 118 or 119 ? checked((int)symbol) : null;
    }

    // 01DC8820：mode0为12B筛选块和CP936字符串；mode1只有一个关闭字节。
    public static RaidSearchRequest Search(ReadOnlySpan<byte> body)
    {
        if (body is [1]) return new(0, 0, "", Closed: true);
        if (body.Length < 17 || body[0] != 0) throw new InvalidDataException("攻坚搜索需要mode0与完整筛选块");
        int length = BinaryPrimitives.ReadInt32LittleEndian(body[13..]);
        if (length < 0 || length > MaxNameBytes || body.Length != 17 + length)
            throw new InvalidDataException("攻坚搜索名称长度无效");
        try { return new(BinaryPrimitives.ReadUInt32LittleEndian(body[1..]), body[9], CharacterName.Encoding.GetString(body[17..])); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("攻坚搜索名称不是CP936", error); }
    }

    public const int MaxNameBytes = 64;

    // KB-20261026T063752-c3b822a45d：0130F140/0130F5F9写低u8计数，但遍历完整列表。
    // 只解析计数一致且L≤255的保守结构；更长列表的可达上界未证，不注册Handler或赋予死亡语义。
    public static RaidMonsterHpRequest MonsterHp(ReadOnlySpan<byte> body)
    {
        if (body.Length < 5 || body.Length != 5 + 4 * body[4])
            throw new InvalidDataException("RAID_MONSTER_HP 长度或条目数错误");
        var entries = new uint[body[4]];
        for (int index = 0; index < entries.Length; index++)
            entries[index] = BinaryPrimitives.ReadUInt32LittleEndian(body[(5 + 4 * index)..]);
        return new(BinaryPrimitives.ReadUInt32LittleEndian(body), entries);
    }

    // 015B66F9..015B6717：本体HP百分比阈值报告，仅一个u32，不携带怪物或死亡身份。
    public static int PreyMonsterHp(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4) throw new InvalidDataException("PREY_RAID_MONSTER_HP需要单个u32");
        return BinaryPrimitives.ReadInt32LittleEndian(body);
    }

    // 015B3800..015B382A：设置Boss HP报警值，原生按一个u32发送。
    public static int PreyBossHpAlarm(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4) throw new InvalidDataException("RAID_SET_BOSS_HP_ALARM需要单个u32");
        return BinaryPrimitives.ReadInt32LittleEndian(body);
    }

    // 0096E843..0096E84B经01DC6180：每2秒赋值symbol212，HP单位为最大值的1/10000。
    public static int? PreyBossHpUnits(ReadOnlySpan<byte> body) => body.Length == 9 &&
        BinaryPrimitives.ReadUInt32LittleEndian(body) == 212 && body[8] == 0
            ? BinaryPrimitives.ReadInt32LittleEndian(body[4..]) : null;

    // NOTI835的023AB9FA..023ABA1B发送REJOIN_RAID：唯一正文为原通知的u32团key。
    public static uint Rejoin(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4) throw new InvalidDataException("REJOIN_RAID 需要u32团key");
        return BinaryPrimitives.ReadUInt32LittleEndian(body);
    }

    // 02174823/0216CCC2编队、03990155委任团长：三个u32；目标虽取自成员u16，03BDF780仍写四字节。
    public static RaidManagerRequest Manager(ReadOnlySpan<byte> body)
    {
        if (body.Length != 12) throw new InvalidDataException("RAID_MANAGER_WORK 需要三个u32");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[8..]));
    }

    public static PreyMarkerRequest PreyMarker(ReadOnlySpan<byte> body)
    {
        if (body.Length != 12) throw new InvalidDataException("普雷战略标记需要三个u32");
        var request = new PreyMarkerRequest(BinaryPrimitives.ReadUInt32LittleEndian(body),
            BinaryPrimitives.ReadUInt32LittleEndian(body[4..]), BinaryPrimitives.ReadUInt32LittleEndian(body[8..]));
        if (request.Operation is not (1 or 2) || request.Group > 2 ||
            request.Operation == 1 && request.Target is not (>= 301 and <= 320) || request.Operation == 2 && request.Target != 0)
            throw new InvalidDataException("普雷战略标记操作、分组或区域无效");
        return request;
    }

    // 01DC6660/01DC7410；失败ACK也必须回传mode/key，不是裸公共失败前缀。
    public static RaidInfoRequest Information(ReadOnlySpan<byte> body)
    {
        if (body.Length != 5 || body[0] > 2) throw new InvalidDataException("REQUEST_RAID_INFO 长度或mode错误");
        return new(body[0], BinaryPrimitives.ReadUInt32LittleEndian(body[1..]));
    }

    // 首次招募走REQUEST/RESPONSE_PEER的mode10（01DC6CBE），不把REJOIN_RAID当首次加入。
    public static RaidPeerRequest Peer(ReadOnlySpan<byte> body, bool response)
    {
        bool length = response ? body.Length == 7 || body.Length == 9 && BinaryPrimitives.ReadUInt16LittleEndian(body[7..]) == 0x85 : body.Length == 13;
        if (!length || body[2] != 10) throw new InvalidDataException("团本申请/回应长度或子型错误");
        return new(BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body[3..]));
    }

    public static ModifyRaidRequest ModifyRaid(ReadOnlySpan<byte> body)
    {
        if (body.Length < 5) throw new InvalidDataException("MODIFY_RAID_INFO 缺少类型或队名长度");
        int count = BinaryPrimitives.ReadInt32LittleEndian(body[1..]);
        if (count is < 0 or > MaxNameBytes || body.Length != 5 + count + 1)
            throw new InvalidDataException("MODIFY_RAID_INFO 队名长度或尾字段非法");
        string name;
        try { name = CharacterName.Encoding.GetString(body.Slice(5, count)); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("MODIFY_RAID_INFO 队名不是CP936", error); }
        return new(body[0], name, body[5 + count]);
    }

    public static CreateRaidRequest CreateRaid(ReadOnlySpan<byte> body)
    {
        if (body.Length < 5) throw new InvalidDataException("CREATE_RAID 缺少类型或队名长度");
        int count = BinaryPrimitives.ReadInt32LittleEndian(body[1..]);
        if (count is < 0 or > MaxNameBytes || body.Length != 5 + count + 4)
            throw new InvalidDataException("CREATE_RAID 队名长度或尾字段非法");
        string name;
        try { name = CharacterName.Encoding.GetString(body.Slice(5, count)); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("CREATE_RAID 队名不是CP936", error); }
        var tail = body[(5 + count)..];
        return new(body[0], name, tail[0], tail[1], tail[2], tail[3]);
    }
}
