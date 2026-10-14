using System.Buffers.Binary;

namespace DfoServer.Network.Builders;

internal static class ImageCommunicationPackets
{
    // 本版公共分派只诊断error，475的flag0回调不消费它；0为中立占位，不命名原服业务类别。
    public static byte[] NeutralRejected() => [0,0,0];

    public static byte[] Used(uint gold)
    {
        var body = new byte[9];
        body[0] = 1;
        // 0234F940读取首u32后不使用；只填0，不将未确认字段命名为NPC或费用。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), gold);
        return body;
    }
}
