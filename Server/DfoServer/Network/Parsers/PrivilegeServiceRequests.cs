using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct PrivilegeRewardRequest(uint Mode, uint Value);

internal static class PrivilegeServiceRequests
{
    public static PrivilegeRewardRequest Reward(ReadOnlySpan<byte> body)
    {
        if (body.Length != 21) throw new InvalidDataException("礼遇领取正文长度错误");
        // 原生978E90/978EF0只赋值末两个u32；前13B不用于购买、期限或奖励授权。
        uint mode = BinaryPrimitives.ReadUInt32LittleEndian(body[13..]);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(body[17..]);
        if (mode > 1 || mode == 0 && value != 0 || mode == 1 && value == 0)
            throw new InvalidDataException("礼遇领取动作无效");
        return new(mode, value);
    }
}
