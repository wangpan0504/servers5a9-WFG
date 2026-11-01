using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct RaidCrossTarget(byte Server, ushort Character);
internal sealed record RaidCrossRequest(RaidCrossTarget[] Targets, uint Coins, ushort AntiMagic, byte Channel);

internal static partial class RaidRequests
{
    // 01DC9830批量最多20人；尾部显示值不是peer或网络端点，不能作资产或准入权威。
    public static RaidCrossRequest CrossRequest(ReadOnlySpan<byte> body)
    {
        if (body.Length < 9 || body[0] is < 1 or > 20 || body.Length != 9 + 3 * body[0] || body[^1] != 0)
            throw new InvalidDataException("跨频道申请的计数、长度或尾字节无效");
        var targets = new RaidCrossTarget[body[0]];
        for (int index = 0; index < targets.Length; index++)
            targets[index] = new(body[1 + 3 * index], BinaryPrimitives.ReadUInt16LittleEndian(body[(2 + 3 * index)..]));
        int tail = 1 + 3 * targets.Length;
        return new(targets, BinaryPrimitives.ReadUInt32LittleEndian(body[tail..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[(tail + 4)..]), body[tail + 6]);
    }

    public static RaidUserRequest CrossResponse(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4) throw new InvalidDataException("跨频道接受需要4字节角色定位");
        return new(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[1..]), body[3]);
    }

    public static RaidCrossTarget CrossCancel(ReadOnlySpan<byte> body)
    {
        if (body.Length != 3) throw new InvalidDataException("切频道失败需要3字节目标定位");
        return new(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[1..]));
    }
}
