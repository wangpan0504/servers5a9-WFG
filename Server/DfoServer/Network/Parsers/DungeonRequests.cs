using System.Buffers.Binary;
using System.Collections.Immutable;

namespace DfoServer.Network.Parsers;

internal readonly record struct DungeonEntryRequest(uint DungeonIndex);
internal readonly record struct DungeonStoryPauseRequest(byte State, byte Type);
internal readonly record struct DungeonCharacterDeathRequest(ushort X, ushort Y, ushort? Context = null);
internal sealed record DungeonMoveRequest(byte X, byte Y, uint Unknown2, uint Unknown6, byte Unknown10, IReadOnlyList<byte> RemainingFields);
internal readonly record struct DungeonRidableOperationRequest(byte Operation, uint ResourceId, byte MapX, byte MapY);
// 目标实例按来源记录的正HP降低量及次数；原生setter统计在夹取前计算，允许overkill，不是服务端伤害模拟。
internal sealed record DungeonDeathParticipant(ushort Kind, ushort ObjectKey, ulong CumulativeHpDecrease, ushort PositiveHpDecreaseCount);
internal sealed record DungeonSinglePlayerDeathStatistics(ulong Unknown6, uint Unknown14, ulong Unknown18, uint Unknown26,
    DungeonDeathParticipant Participant, byte RewriteFlag, uint SpawnLevel, ulong SpawnHpSnapshot,
    uint BodyChecksum, bool ChecksumMatches, IReadOnlyList<byte> RemainingFields)
{
    public ImmutableArray<DungeonDeathParticipant> Participants { get; init; } = [Participant];
}
internal sealed record DungeonDeathRequest(uint ObjectKey, ushort Reporter, IReadOnlyList<byte> RemainingFields)
{
    public DungeonSinglePlayerDeathStatistics? SinglePlayerStatistics { get; init; }
}
// 02A787A0独立发送分支；+1754来自构造参数及资源查询，不是普通怪物对象key。
internal sealed record DungeonPassiveDeathRequest(uint ObjectIdentifier, uint Unknown42, byte Unknown36,
    ushort Unknown39, byte UnknownLast, bool ChecksumMatches, IReadOnlyList<byte> ContextFields);
internal sealed record DungeonScriptedDeathRequest(uint ObjectKey, byte RewriteFlag, bool ChecksumMatches, ushort Reporter);

// Unknown 后的数字为正文偏移；保留未确认字段，不推断常量域或业务枚举。
internal readonly record struct DungeonSelectionRequest(
    uint DungeonIndex,
    byte Parameter,
    ushort Unknown5,
    byte Unknown7,
    byte Unknown8,
    ushort Unknown9,
    uint Unknown11,
    byte Unknown15,
    uint Unknown16,
    byte Unknown20,
    uint Unknown21);

internal static class DungeonRequests
{
    public static bool TryParseRidableOperation(ReadOnlySpan<byte> body, out DungeonRidableOperationRequest request)
    {
        request = default;
        // 三个原生生产者均直接写7B；本版316不追加通用正文校验和。
        if (body.Length != 7 || body[0] is < 1 or > 3) return false;
        request = new(body[0], BinaryPrimitives.ReadUInt32LittleEndian(body[1..]), body[5], body[6]);
        return true;
    }

    public static bool TryParseCharacterDeath(ReadOnlySpan<byte> body, out DungeonCharacterDeathRequest request)
    {
        request = default;
        if (body.Length is not (4 or 6)) return false;
        request = new(BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[2..]),
            body.Length == 6 ? BinaryPrimitives.ReadUInt16LittleEndian(body[4..]) : null);
        return true;
    }

    public static bool TryParseUseCoin(ReadOnlySpan<byte> body, out ushort targetKey)
    {
        targetKey = 0;
        if (body.Length != 2) return false;
        targetKey = BinaryPrimitives.ReadUInt16LittleEndian(body);
        return true;
    }

    public static bool TryParseStoryPause(ReadOnlySpan<byte> body, out DungeonStoryPauseRequest request)
    {
        request = default;
        if (body.Length != 2 || body[0] > 1 || body[1] > 2) return false;
        request = new(body[0],body[1]);
        return true;
    }

    // 选择界面返回实收为空正文；未知形状不猜造应答。
    public static bool IsSelectionReturn(ReadOnlySpan<byte> body) => body.IsEmpty;

    public static bool TryParseDeath(ReadOnlySpan<byte> body, out DungeonDeathRequest? request)
    {
        request = null;
        // 旧88B诊断合同保留；Full03动态writer和友方APC实收另确认74+14*count，无额外前缀。
        bool parsed = TryParseSinglePlayerDeathStatistics(body, out var statistics);
        if (body.Length != 88 && !parsed) return false;
        request = new(BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[4..]),
            Array.AsReadOnly(body[6..].ToArray())) { SinglePlayerStatistics = statistics };
        return true;
    }

    public static bool TryParsePassiveDeath(ReadOnlySpan<byte> body, out DungeonPassiveDeathRequest? request)
    {
        request = null;
        // 直接writer共43B，029083A0另写5B或27B，通用发送追加4B：总长52B或74B。
        // 常量域来自该生产者；公共helper的22B上下文原样保留，不套用普通怪物HP统计。
        if (body.Length is not (52 or 74) || BinaryPrimitives.ReadUInt16LittleEndian(body[4..]) != ushort.MaxValue ||
            body[6..36].ContainsAnyExcept((byte)0) || body[37] != 1 || body[38] != 0 || body[41] != 0 || body[^6] != 0)
            return false;
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(body[^4..]);
        request = new(BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body[42..]),
            body[36], BinaryPrimitives.ReadUInt16LittleEndian(body[39..]), body[^5],
            checksum == ClientBodyChecksum.Compute(body[..^4]), Array.AsReadOnly(body[46..^6].ToArray()));
        return true;
    }

    public static bool TryParseScriptedDeath(ReadOnlySpan<byte> body, out DungeonScriptedDeathRequest? request)
    {
        request = null;
        // 普通对象生产者的count=0分支，共74B；零参与者不限于剧情演员，且独立于被动物件生产者。
        // reporter可为实际角色号或FFFF；zz的3151撤退演员实际回报角色号，不能把FFFF当资格。
        if (body.Length != 74 || body[30] != 0)
            return false;
        // 统计尾从31开始，前10B仍为统计字段；029083A0在41写改写标志，再写等级与创建HP。
        request = new(BinaryPrimitives.ReadUInt32LittleEndian(body),body[41],
            BinaryPrimitives.ReadUInt32LittleEndian(body[70..]) == ClientBodyChecksum.Compute(body[..70]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[4..]));
        return true;
    }

    internal static bool TryParseSinglePlayerDeathStatistics(ReadOnlySpan<byte> body, out DungeonSinglePlayerDeathStatistics? statistics)
    {
        statistics = null;
        // 02B533D0写u8 count，02B533F0..02B53493逐行写u16/u16/u64/u16；最多254行，顺序不是killer顺序。
        if (body.Length < 88 || body[30] is 0 or 255 || body.Length != 74 + 14*body[30])
            return false;
        var participants = ImmutableArray.CreateBuilder<DungeonDeathParticipant>(body[30]);
        for (int at = 31; at < 31 + 14*body[30]; at += 14)
            participants.Add(new(BinaryPrimitives.ReadUInt16LittleEndian(body[at..]),
                BinaryPrimitives.ReadUInt16LittleEndian(body[(at+2)..]), BinaryPrimitives.ReadUInt64LittleEndian(body[(at+4)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(body[(at+12)..])));
        int tail = 31 + 14*body[30];
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(body[^4..]);
        // 029083A0写改写标志、等级getter及this+1140的创建HP快照；快照不是新恢复的原版maxHP公式。
        // 末4B由通用发送前处理追加。这里只报告检查结果，原诊断长度接受行为不据此改变。
        statistics = new(BinaryPrimitives.ReadUInt64LittleEndian(body[6..]), BinaryPrimitives.ReadUInt32LittleEndian(body[14..]),
            BinaryPrimitives.ReadUInt64LittleEndian(body[18..]), BinaryPrimitives.ReadUInt32LittleEndian(body[26..]),
            participants[0], body[tail+10], BinaryPrimitives.ReadUInt32LittleEndian(body[(tail+11)..]), BinaryPrimitives.ReadUInt64LittleEndian(body[(tail+15)..]),
            checksum, checksum == ClientBodyChecksum.Compute(body[..^4]), Array.AsReadOnly(body[tail..].ToArray()))
        { Participants = participants.ToImmutable() };
        return true;
    }
    public static bool TryParseMove(ReadOnlySpan<byte> body, out DungeonMoveRequest? request)
    {
        request = null;
        // 01DE471B 路径含 01DE2560 的统计尾部，共 100B；其中 85..92 是完整 u64。
        // 目标格之后的字段尚不承担服务端通关或伤害权威，仅保留全部原字节。
        if (body.Length != 100)
            return false;
        request = new(body[0], body[1], BinaryPrimitives.ReadUInt32LittleEndian(body[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[6..]), body[10], Array.AsReadOnly(body[11..].ToArray()));
        return true;
    }

    public static DungeonEntryRequest ParseEntry(ReadOnlySpan<byte> body)
    {
        ValidateSelection(body);
        return new(BinaryPrimitives.ReadUInt32LittleEndian(body));
    }

    public static void ValidateSelection(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4)
            throw new InvalidDataException("Invalid dungeon-selection request");
    }

    public static bool TryParseSelection(ReadOnlySpan<byte> body, out DungeonSelectionRequest request)
    {
        request = default;
        // 这里只识别已确认的 25 字节发送路径，其他宽度交回未实现分派。
        if (body.Length != 25)
            return false;
        request = new(
            BinaryPrimitives.ReadUInt32LittleEndian(body),
            body[4],
            BinaryPrimitives.ReadUInt16LittleEndian(body[5..]),
            body[7],
            body[8],
            BinaryPrimitives.ReadUInt16LittleEndian(body[9..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[11..]),
            body[15],
            BinaryPrimitives.ReadUInt32LittleEndian(body[16..]),
            body[20],
            BinaryPrimitives.ReadUInt32LittleEndian(body[21..]));
        return true;
    }
}
