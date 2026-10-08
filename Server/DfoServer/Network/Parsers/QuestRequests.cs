using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct QuestFinishRequest(ushort QuestId, ushort RewardSelector, ushort Multiplier, ushort Reserved);
internal readonly record struct QuestTriggerRequest(ushort QuestId, byte Value, byte Reserved);

internal static class QuestRequests
{
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
