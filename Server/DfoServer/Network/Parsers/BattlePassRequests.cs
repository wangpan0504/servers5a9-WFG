using System.Buffers.Binary;
using System.Collections.Immutable;

namespace DfoServer.Network.Parsers;

internal readonly record struct BattlePassMissionRequest(ImmutableArray<byte> OpaquePrefix13, byte RawActionCode,
    ushort RowKey, uint RawCategoryCode, uint RowIndex);
internal readonly record struct BattlePassRewardRequest(ImmutableArray<byte> OpaquePrefix13, byte RawActionCode,
    byte RawTrackCode, uint Key);

internal static class BattlePassRequests
{
    // 标准正文透传下，旧发送器把13B不透明前缀连同业务尾一并追加；公共外帧头已经另行剥除。
    // 此处只拆结构并保留未知raw值，不校验业务枚举、不推进任务或领取奖励。
    public static BattlePassMissionRequest Mission(ReadOnlySpan<byte> body)
    {
        if (body.Length != 24) throw new InvalidDataException("战令任务请求必须为完整24字节正文");
        return new(ImmutableArray.Create(body[..13].ToArray()), body[13],
            BinaryPrimitives.ReadUInt16LittleEndian(body[14..]), BinaryPrimitives.ReadUInt32LittleEndian(body[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[20..]));
    }

    public static BattlePassRewardRequest Reward(ReadOnlySpan<byte> body)
    {
        if (body.Length != 19) throw new InvalidDataException("战令奖励请求必须为完整19字节正文");
        return new(ImmutableArray.Create(body[..13].ToArray()), body[13], body[14],
            BinaryPrimitives.ReadUInt32LittleEndian(body[15..]));
    }
}
