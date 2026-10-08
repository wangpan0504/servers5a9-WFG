using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal sealed record DungeonBossCheckRequest(ushort ActorId, ushort BossKey, uint SpawnLevel,
    ulong SpawnHp, IReadOnlyList<byte> RemainingFields);
internal sealed record DungeonPlayResultRequest(ushort ActorId, ushort ClearTime, uint ParticipantValue,
    byte RankPoint, byte MaximumRankPoint, IReadOnlyList<byte> RemainingFields);

internal static class DungeonSettlementRequests
{
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
