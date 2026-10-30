using System.Buffers.Binary;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Builders;

internal static class OrdinaryLukeSequencePackets
{
    // 0239DC70：u32序列、u8阶段、u32通关位、u32未知、u8尾标志、u16尾值。
    public static byte[] Information(OrdinaryLukeSequenceInfo info)
    {
        var body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body, OrdinaryLukeProgress.SequenceId);
        body[4] = info.Stage;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), info.ClearBits);
        // 序列42未声明第四字段对应资源开关；最后两字段仅序列28消费。
        return body;
    }
}
