using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Inventory;

internal static class EpicPieceShopRequests
{
    public static int Select(ReadOnlySpan<byte> body) => body.Length == 4 ? BinaryPrimitives.ReadInt32LittleEndian(body) :
        throw new InvalidDataException("CMD818必须包含一个目标碎片编号i32");
    public static void Refresh(ReadOnlySpan<byte> body)
    {
        if (!body.IsEmpty) throw new InvalidDataException("CMD819必须为空正文");
    }
    public static byte Complete(ReadOnlySpan<byte> body) => body.Length == 1 && body[0] <= 1 ? body[0] :
        throw new InvalidDataException("CMD820必须包含一个0或1的兑换方式字节");
}
