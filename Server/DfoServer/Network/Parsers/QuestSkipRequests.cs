using System.Buffers.Binary;
using System.Collections.Immutable;

namespace DfoServer.Network.Parsers;

internal readonly record struct QuestQuickClearRequest(uint ParentKey);
internal readonly record struct QuestBranchAnswer(uint QuestionQuestId, uint Answer);

internal static class QuestSkipRequests
{
    public static bool TryQuickClear(ReadOnlySpan<byte> body, out QuestQuickClearRequest request)
    {
        request = default;
        // 1714 的 producer 直接写 connect unit 的 parent key，没有正文 opcode 前缀。
        if (body.Length != 4) return false;
        request = new(BinaryPrimitives.ReadUInt32LittleEndian(body));
        return true;
    }

    public static bool TryClearBranch(ReadOnlySpan<byte> body, out ImmutableArray<QuestBranchAnswer> answers)
    {
        answers = default;
        if (body.IsEmpty || body.Length != 1 + body[0] * 8) return false;
        int count = body[0];
        var parsed = ImmutableArray.CreateBuilder<QuestBranchAnswer>(count);
        for (int index = 0; index < count; index++)
        {
            int offset = 1 + index * 8;
            parsed.Add(new(BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]),
                BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 4)..])));
        }
        // 1431 的答案是零基 u32，保留值与原顺序，资源资格交由业务层判断。
        answers = parsed.MoveToImmutable();
        return true;
    }
}
