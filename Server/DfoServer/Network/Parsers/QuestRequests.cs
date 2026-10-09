using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct QuestFinishRequest(ushort QuestId, ushort RewardSelector, ushort Multiplier, ushort Reserved);
internal readonly record struct QuestTriggerRequest(ushort QuestId, byte Value, byte Reserved);
internal readonly record struct QuestClientTriggerRequest(ushort QuestId, byte? Channel, byte Value, byte Reserved);
internal readonly record struct QuestHiddenOpenRequest(uint QuestId, uint NpcDialog, uint ItemUseCheck,
    uint ItemUseState, uint StandByFirst, uint StandBySecond, uint ClientState);

internal static class QuestRequests
{
    public static bool TryOpenHidden(ReadOnlySpan<byte> body, out QuestHiddenOpenRequest request)
    {
        request = default;
        // Full03 02289490逐项写七个u32；884正文没有额外opcode前缀。
        if (body.Length != 28) return false;
        uint questId = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (questId is 0 or >= 30000) return false;
        request = new(questId, BinaryPrimitives.ReadUInt32LittleEndian(body[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[8..]), BinaryPrimitives.ReadUInt32LittleEndian(body[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[16..]), BinaryPrimitives.ReadUInt32LittleEndian(body[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[24..]));
        return true;
    }

    public static bool TryTrigger(ReadOnlySpan<byte> body, out QuestClientTriggerRequest request)
    {
        request = default;
        if (body.Length is not (6 or 7) || BinaryPrimitives.ReadUInt16LittleEndian(body) != (ushort)CmdPacketType.SET_QUEST_TRIGGER)
            return false;
        // 022806CB..06E4只在10/20/40通道写额外字节；未知通道不能借7B形状入库。
        if (body.Length == 7 && body[4] is not (0x10 or 0x20 or 0x40)) return false;
        request = new(BinaryPrimitives.ReadUInt16LittleEndian(body[2..]), body.Length == 7 ? body[4] : null,
            body[^2], body[^1]);
        return true;
    }
    public static bool TryAccept(ReadOnlySpan<byte> body, out ushort questId) =>
        TryIdentity(body, (ushort)CmdPacketType.ACCEPT_QUEST, out questId);

    public static bool TryGiveup(ReadOnlySpan<byte> body, out ushort questId) =>
        TryIdentity(body, (ushort)CmdPacketType.GIVEUP_QUEST, out questId);

    private static bool TryIdentity(ReadOnlySpan<byte> body, ushort opcode, out ushort questId)
    {
        questId = 0;
        if (body.Length != 4 || BinaryPrimitives.ReadUInt16LittleEndian(body) != opcode)
            return false;
        questId = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        return true;
    }

    public static bool TryFinish(ReadOnlySpan<byte> body, out QuestFinishRequest request)
    {
        request = default;
        if (body.Length != 10 || BinaryPrimitives.ReadUInt16LittleEndian(body) != (ushort)CmdPacketType.FINISH_QUEST)
            return false;
        request = new(BinaryPrimitives.ReadUInt16LittleEndian(body[2..]), BinaryPrimitives.ReadUInt16LittleEndian(body[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[6..]), BinaryPrimitives.ReadUInt16LittleEndian(body[8..]));
        return true;
    }

    public static bool TrySingleClearTrigger(ReadOnlySpan<byte> body, out QuestTriggerRequest request)
    {
        request = default;
        if (body.Length != 6 || BinaryPrimitives.ReadUInt16LittleEndian(body) != (ushort)CmdPacketType.SET_QUEST_TRIGGER)
            return false;
        request = new(BinaryPrimitives.ReadUInt16LittleEndian(body[2..]), body[4], body[5]);
        return true;
    }
}
