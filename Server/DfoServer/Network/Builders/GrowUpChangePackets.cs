using System.Buffers.Binary;

namespace DfoServer.Network.Builders;

internal static class GrowUpChangePackets
{
    // 012A1DB5：result133 创建收费确认窗口，回调 0129D3E0 再发 confirm=1。
    public const int QuoteResult = 133;

    // 公共成功前缀后 012A1D73 读取 RAW5 = result:i32 + GrowUpChangeCount:sbyte；result0 把末字节写入 062C188C。
    public static byte[] Committed(int count) => Success(0, count);
    public static byte[] Quote(int count) => Success(QuoteResult, count);

    static byte[] Success(int result, int count)
    {
        if (count is < 0 or > sbyte.MaxValue) throw new ArgumentOutOfRangeException(nameof(count));
        var body = new byte[6];
        body[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), result);
        body[5] = (byte)count;
        return body;
    }

    // 公共失败前缀 success=0 + error:u16；012A2034 不再读取正文。
    public static byte[] Failure(ushort error)
    {
        if (error == 0) throw new ArgumentOutOfRangeException(nameof(error));
        byte[] body = [0, 0, 0];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), error);
        return body;
    }

    // NOTI1153 CHARACTER_MANAGE_INFO：01299C78 读取 count:sbyte + level:i32，写 062C188C/062C1890。
    public static byte[] ManageInfo(int count, int level)
    {
        if (count is < 0 or > sbyte.MaxValue) throw new ArgumentOutOfRangeException(nameof(count));
        var body = new byte[5];
        body[0] = (byte)count;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), level);
        return body;
    }
}
