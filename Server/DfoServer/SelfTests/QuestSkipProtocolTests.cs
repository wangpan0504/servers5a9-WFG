using System.Buffers.Binary;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestSkipProtocolTests
{
    public static void Run()
    {
        int before = Count;
        foreach (var (hex, parentKey) in new (string, uint)[]
        {
            ("13000000", 19), ("15000000", 21), ("17000000", 23),
            ("00000000", 0), ("FFFFFFFF", uint.MaxValue)
        })
            That(QuestSkipRequests.TryQuickClear(Convert.FromHexString(hex), out var request) &&
                request == new QuestQuickClearRequest(parentKey), "1714 保留完整 parent key " + parentKey);
        foreach (byte[] bad in new byte[][] { [], [19], [19,0,0], [19,0,0,0,0], [0xB2,6,19,0,0,0] })
            That(!QuestSkipRequests.TryQuickClear(bad, out _), "1714 拒绝截断、附加尾部和 opcode 正文前缀");

        byte[] multiple = Convert.FromHexString("034918000000000000EE18000001000000EF18000002000000");
        That(QuestSkipRequests.TryClearBranch(multiple, out var answers) &&
            answers.SequenceEqual(new QuestBranchAnswer[] { new(6217,0), new(6382,1), new(6383,2) }),
            "1431 多问题按原顺序保留零基答案，无 opcode 前缀");
        That(QuestSkipRequests.TryClearBranch([0], out var empty) && empty.IsEmpty,
            "1431 零记录正文由业务层决定用途");
        That(QuestSkipRequests.TryClearBranch(Convert.FromHexString("02FFFFFFFFFFFFFFFF00000000FFFFFFFF"), out var full) &&
            full.SequenceEqual(new QuestBranchAnswer[] { new(uint.MaxValue,uint.MaxValue), new(0,uint.MaxValue) }),
            "1431 完整保留 u32 问题和答案，不在 parser 缩窄语义范围");
        That(QuestSkipRequests.TryClearBranch(Convert.FromHexString("0249180000000000004918000002000000"), out var repeated) &&
            repeated.SequenceEqual(new QuestBranchAnswer[] { new(6217,0), new(6217,2) }),
            "1431 重复问题保持原始记录，是否冲突交由业务层校验");
        for (int length = 0; length < multiple.Length; length++)
            That(!QuestSkipRequests.TryClearBranch(multiple.AsSpan(0,length), out _), "1431 拒绝截断长度 " + length);
        foreach (byte[] bad in new byte[][]
        {
            [0,0], [1], [2, .. multiple.AsSpan(1).ToArray()], [.. multiple,0], [0x97,5, .. multiple]
        })
            That(!QuestSkipRequests.TryClearBranch(bad, out _), "1431 拒绝数量不匹配、附加尾部和 opcode 正文前缀");

        byte[] maximum = new byte[1 + byte.MaxValue * 8]; maximum[0] = byte.MaxValue;
        for (int index = 0; index < byte.MaxValue; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(maximum.AsSpan(1 + index * 8), (uint)index);
            BinaryPrimitives.WriteUInt32LittleEndian(maximum.AsSpan(5 + index * 8), uint.MaxValue - (uint)index);
        }
        That(QuestSkipRequests.TryClearBranch(maximum, out var all) && all.Length == byte.MaxValue &&
            all[0] == new QuestBranchAnswer(0,uint.MaxValue) && all[^1] == new QuestBranchAnswer(254,uint.MaxValue - 254),
            "1431 完整支持 count:u8，不将活动任务槽数量作为协议限制");
        Console.WriteLine($"PASS {Count - before} quest skip request protocol assertions");
    }
}
