using System.Buffers.Binary;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestHiddenProtocolTests
{
    public static void RunCore()
    {
        int before = Count;
        byte[] body = Convert.FromHexString("2A2F0000FFFFFFFF00000080674523812A000000FFFF0000EFBEEDFE");
        That(QuestRequests.TryOpenHidden(body,out var request) &&
            request == new QuestHiddenOpenRequest(12074,uint.MaxValue,0x80000000,0x81234567,42,65535,0xFEEDBEEF),
            "884严格读取七个u32，保留非零上下文及原始有符号位");
        That(unchecked((int)request.NpcDialog) == -1 && unchecked((int)request.ItemUseCheck) == int.MinValue &&
            request.ItemUseState == 0x81234567 && request.ClientState == 0xFEEDBEEF,
            "884不将上下文缩窄、归零或解释为任务进度计数");
        for (int length = 0; length < body.Length; length++)
            That(!QuestRequests.TryOpenHidden(body.AsSpan(0,length),out var truncated) && truncated == default,
                "884拒绝截断正文且无部分请求：" + length);
        foreach (byte[] invalid in new byte[][] { [.. body,0], [.. body,0,0], [0x74,0x03, .. body] })
            That(!QuestRequests.TryOpenHidden(invalid,out var rejected) && rejected == default,
                "884拒绝额外尾部与错误的opcode正文前缀");
        foreach (uint id in new uint[] { 1,29999 })
        {
            byte[] valid = (byte[])body.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(valid,id);
            That(QuestRequests.TryOpenHidden(valid,out var boundary) && boundary.QuestId == id &&
                boundary.ClientState == request.ClientState,
                "884接受任务编号边界并保留其余字段：" + id);
        }
        foreach (uint id in new uint[] { 0,30000,65535,uint.MaxValue })
        {
            byte[] invalid = (byte[])body.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(invalid,id);
            That(!QuestRequests.TryOpenHidden(invalid,out var rejected) && rejected == default,
                "884拒绝非法任务编号，不截断u32：" + id);
        }

        Bytes(QuestPackets.OpenHidden([12074,1,29999]),
            Convert.FromHexString("030000002A2F0000010000002F750000"),
            "822为count:u32与原序qid:u32，无成功字节或额外opcode");
        Bytes(QuestPackets.OpenHidden([]),[0,0,0,0],"822空数组仍编码完整count:u32");
        foreach (int[] ids in new int[][] { [12074,12074], [-1], [0], [30000], [65535] })
            Reject<InvalidDataException>(() => QuestPackets.OpenHidden(ids),
                "822拒绝重复或非法编号，不静默去重或截断");
        Console.WriteLine($"PASS {Count - before} hidden quest protocol assertions");
    }
}
