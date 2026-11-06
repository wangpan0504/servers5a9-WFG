using System.Buffers.Binary;
using System.Collections.Immutable;

namespace DfoServer.Network.Parsers;

internal readonly record struct MonsterMockBattleRequest(ImmutableArray<byte> OpaquePrefix13,
    uint Operation, uint Argument0, uint Argument1, ImmutableArray<byte> Payload20);

internal static class MonsterMockBattleRequests
{
    public static MonsterMockBattleRequest Parse(ReadOnlySpan<byte> body)
    {
        // 本版004780E0发送完整45B；13B前缀不提供账号、角色或操作资格。
        if (body.Length != 45) throw new InvalidDataException("怪物模拟战请求必须为完整45字节正文");
        return new(ImmutableArray.Create(body[..13].ToArray()),
            BinaryPrimitives.ReadUInt32LittleEndian(body[13..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[17..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[21..]),
            ImmutableArray.Create(body[25..].ToArray()));
    }
}
