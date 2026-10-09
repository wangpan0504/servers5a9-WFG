using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal static class QuestReplayRequests
{
    // Full03 01617B20生产一个QST编号，路线来自connectquestlist，不从请求补造。
    public static bool TryParse(ReadOnlySpan<byte> body,out uint questId)
    {
        questId = 0;
        if (body.Length != 4) return false;
        questId = BinaryPrimitives.ReadUInt32LittleEndian(body);
        return true;
    }
}
