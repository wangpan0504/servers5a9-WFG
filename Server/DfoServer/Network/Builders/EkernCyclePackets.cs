using System.Buffers.Binary;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Builders;

internal static class EkernCyclePackets
{
    // 0239E1D0：u8条数，每行四个i32。首项为副本，第二项为已通关；后两项当前未消费，保持零。
    public static byte[] Info(EkernCycleProgress progress)
    {
        byte[] body = new byte[33]; body[0] = 2;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1),5097);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5),progress.Normal);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(17),5098);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(21),progress.Trial);
        return body;
    }
}
