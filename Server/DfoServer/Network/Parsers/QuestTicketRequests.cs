using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct QuestTicketUseRequest(ushort Slot,byte List,uint Reserved,uint Action,uint FirstArgument,uint SecondArgument);

internal static class QuestTicketRequests
{
    // Full03 028BA3F0：19B正文，无额外opcode或任务ID前缀。
    public static bool TryUse(ReadOnlySpan<byte> body,out QuestTicketUseRequest request)
    {
        request = default;
        if (body.Length != 19) return false;
        request = new(BinaryPrimitives.ReadUInt16LittleEndian(body),body[2],BinaryPrimitives.ReadUInt32LittleEndian(body[3..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[7..]),BinaryPrimitives.ReadUInt32LittleEndian(body[11..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[15..]));
        return true;
    }
}
