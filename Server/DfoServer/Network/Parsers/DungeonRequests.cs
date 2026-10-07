using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct DungeonEntryRequest(uint DungeonIndex);
internal sealed record DungeonMoveRequest(byte X, byte Y, uint Unknown2, uint Unknown6, byte Unknown10, IReadOnlyList<byte> RemainingFields);
internal sealed record DungeonDeathRequest(uint ObjectKey, ushort Reporter, IReadOnlyList<byte> RemainingFields);

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
    public static bool TryParseDeath(ReadOnlySpan<byte> body, out DungeonDeathRequest? request)
    {
        request = null;
        // 仅识别当前普通怪物实收的 88B 路径；其统计尾部不解释为奖励或权威伤害。
        if (body.Length != 88) return false;
        request = new(BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[4..]),
            Array.AsReadOnly(body[6..].ToArray()));
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
