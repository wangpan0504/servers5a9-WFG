using System.Buffers.Binary;

namespace DfoServer.Network.Builders;

internal static class QuestSkipPackets
{
    public static byte[] Questions(IReadOnlyList<int> questions)
    {
        if (questions.Count is < 1 or > byte.MaxValue || questions.Distinct().Count() != questions.Count ||
            questions.Any(id => id is < 1 or >= 30000))
            throw new InvalidDataException("1381需要非空、唯一的真实问题ID原序");
        var body = new byte[1+questions.Count*4];
        body[0] = checked((byte)questions.Count);
        for (int index = 0; index < questions.Count; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1+index*4),checked((uint)questions[index]));
        return body;
    }
}
