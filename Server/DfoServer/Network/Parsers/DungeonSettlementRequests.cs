using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

// 普通单人C2S46的49B直接正文，未知统计不作为通关资格、奖励金额或请求票据。
internal sealed record DungeonOrdinaryPlayResultRequest(byte Mode, ushort FirstStatistic, ushort CharacterKey,
    uint ParticipantStatistic, byte ParticipantFlag, byte BestFlag, uint SecondStatistic, uint ThirdStatistic,
    IReadOnlyList<uint> CharacterStatistics, byte Grade, uint FourthStatistic, uint FifthStatistic)
{
    public uint? BodyChecksum { get; init; }
    public bool? ChecksumMatches { get; init; }
}

internal readonly record struct DungeonCardSelectionRequest(byte Type, byte MemberIndex);

internal sealed record DungeonBossCheckRequest(ushort ActorId, ushort BossKey, uint SpawnLevel,
    ulong SpawnHp, IReadOnlyList<byte> RemainingFields);

internal sealed record DungeonPlayResultRequest(ushort ActorId, ushort ClearTime, uint ParticipantValue,
    byte RankPoint, byte MaximumRankPoint, IReadOnlyList<byte> RemainingFields);

internal static class DungeonSettlementRequests
{
    // 039EAF35建立69头后直接发送；只接受完整空正文。
    public static bool TryParseScoreScroll(ReadOnlySpan<byte> body) => body.IsEmpty;

    // 普通03A01B10保护桥真实发送70空正文，69..72 checksum表不追加摘要。
    public static bool TryParseCardRights(ReadOnlySpan<byte> body) => body.IsEmpty;

    // 039ED4A0/039ED590分别发送type0免费/type1付费及成员索引；本地卡槽不在wire。
    public static bool TryParseCardSelection(ReadOnlySpan<byte> body, out DungeonCardSelectionRequest request)
    {
        request = default;
        if (body.Length != 2 || body[0] > 1 || body[1] > 7)
            return false;
        request = new(body[0], body[1]);
        return true;
    }

    // 039EBBB0写mode/operation两个u8；mode1提交，mode2只更新悬停选择。
    // 普通39F7FC0及实际dstr479/480/481确认：0再挑战、1选择其他副本、2回城。
    public static bool TryParseOrdinaryExit(ReadOnlySpan<byte> body, out byte operation)
    {
        operation = 0;
        if (body.Length != 2 || body[0] != 1 || body[1] > 2)
            return false;
        operation = body[1];
        return true;
    }

    public static bool TryParseOrdinary(ReadOnlySpan<byte> body, out DungeonOrdinaryPlayResultRequest? request)
    {
        request = null;
        // 02CCD800普通sender首u8=0；支持count1已证单人形状，不截断特殊副本附加正文。
        if (body.Length is not (49 or 53) || body[0] != 0 || body[3] != 1)
            return false;
        var characterStatistics = new uint[5];
        for (int index = 0; index < characterStatistics.Length; index++)
            characterStatistics[index] = BinaryPrimitives.ReadUInt32LittleEndian(body[(20 + index * 4)..]);
        uint? checksum = body.Length == 53 ? BinaryPrimitives.ReadUInt32LittleEndian(body[49..]) : null;
        request = new(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[1..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[4..]), BinaryPrimitives.ReadUInt32LittleEndian(body[6..]),
            body[10], body[11], BinaryPrimitives.ReadUInt32LittleEndian(body[12..]), BinaryPrimitives.ReadUInt32LittleEndian(body[16..]),
            Array.AsReadOnly(characterStatistics), body[40], BinaryPrimitives.ReadUInt32LittleEndian(body[41..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[45..]))
        {
            BodyChecksum = checksum,
            ChecksumMatches = checksum.HasValue ? checksum.Value == ClientBodyChecksum.Compute(body[..49]) : null
        };
        return true;
    }

    public static bool TryParseBossCheck(ReadOnlySpan<byte> body, out DungeonBossCheckRequest? request)
    {
        request = null;
        // 普通单人路径为39B；此命令没有通用正文检查值，统计尾保持原字节。
        if (body.Length != 39) return false;
        request = new(BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[13..]), BinaryPrimitives.ReadUInt64LittleEndian(body[17..]),
            Array.AsReadOnly(body[4..].ToArray()));
        return true;
    }

    public static bool TryParsePlayResult(ReadOnlySpan<byte> body, out DungeonPlayResultRequest? request)
    {
        request = null;
        // 普通单人：49B原生报告及4B检查值。其他成员数和特殊副本扩展不套本合同。
        if (body.Length != 53 || body[0] != 0 || body[3] != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[49..]) != ClientBodyChecksum.Compute(body[..49]))
            return false;
        request = new(BinaryPrimitives.ReadUInt16LittleEndian(body[4..]), BinaryPrimitives.ReadUInt16LittleEndian(body[1..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[6..]), body[10], body[11], Array.AsReadOnly(body[12..49].ToArray()));
        return true;
    }
}
