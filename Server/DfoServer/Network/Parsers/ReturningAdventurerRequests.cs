using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct ReturningAdventurerRequest(uint Action, uint Argument);
internal static class ReturningAdventurerRequests
{
    // 原生00498410/00498470/004984D0发送21B正文，前13B为未初始化栈内容，不作为业务输入。
    public static ReturningAdventurerRequest? Read(ReadOnlySpan<byte> body) => body.Length == 21
        ? new(BinaryPrimitives.ReadUInt32LittleEndian(body[13..]), BinaryPrimitives.ReadUInt32LittleEndian(body[17..])) : null;
}
