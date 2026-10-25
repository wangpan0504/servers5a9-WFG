using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal sealed record DungeonBossCheckRequest(ushort ActorId, ushort BossKey, uint SpawnLevel,
    ulong SpawnHp, IReadOnlyList<byte> RemainingFields);
internal sealed record DungeonPlayResultParticipant(ushort ActorId, uint Value, byte RankPoint);
internal sealed record DungeonPlayResultRequest(ushort ClearTime, IReadOnlyList<DungeonPlayResultParticipant> Participants,
    byte MaximumRankPoint, IReadOnlyList<byte> RemainingFields)
{
    public ushort ActorId => Participants[0].ActorId;
    public byte RankPoint => Participants[0].RankPoint;
}

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
        // 普通报告：4B头、每名成员7B、最高评分1B、统计尾37B及检查值4B。
        if (body.Length < 53 || body[0] != 0 || body[3] is < 1 or > 8 || body.Length != 46 + 7 * body[3])
            return false;
        int checksumAt = body.Length - 4, maximumAt = 4 + 7 * body[3];
        if (BinaryPrimitives.ReadUInt32LittleEndian(body[checksumAt..]) != ClientBodyChecksum.Compute(body[..checksumAt]))
            return false;
        var members = new DungeonPlayResultParticipant[body[3]];
        var actors = new HashSet<ushort>();
        for (int index = 0; index < members.Length; index++)
        {
            int at = 4 + index * 7;
            ushort actor = BinaryPrimitives.ReadUInt16LittleEndian(body[at..]);
            if (!actors.Add(actor)) return false;
            members[index] = new(actor, BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 2)..]), body[at + 6]);
        }
        if (body[maximumAt] != members.Max(member => member.RankPoint)) return false;
        request = new(BinaryPrimitives.ReadUInt16LittleEndian(body[1..]), Array.AsReadOnly(members), body[maximumAt],
            Array.AsReadOnly(body[(maximumAt + 1)..checksumAt].ToArray()));
        return true;
    }
}
