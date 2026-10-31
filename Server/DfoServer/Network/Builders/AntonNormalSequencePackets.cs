using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Network.Builders;

internal static class AntonNormalSequencePackets
{
    public static byte[] Information(AntonNormalSequenceInfo info)
    {
        var body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body, AntonNormalProgress.SequenceId);
        body[4] = info.Stage;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), info.ClearBits);
        // 分支键沿序列28资源四条路线；尾3字节未接入已证业务，保留零值。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), info.Branch);
        return body;
    }
}
