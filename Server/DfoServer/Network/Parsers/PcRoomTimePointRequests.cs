namespace DfoServer.Network.Parsers;

internal readonly record struct PcRoomTimePointRequest(byte Type, byte Index);

internal static class PcRoomTimePointRequests
{
    // V31：02200665 查询；02233C17 起的按钮回调逐字节写入正文，13B帧头由03BDF610另写。
    public static bool TryParse(ReadOnlySpan<byte> body, out PcRoomTimePointRequest request)
    {
        request = default;
        if (body.Length != 2) return false;
        byte type = body[0], index = body[1];
        bool valid = index == byte.MaxValue && type is 0 or 1 or 2 or 4 or 8 || type == 0x10 && index <= 4;
        if (!valid) return false;
        request = new(type, index);
        return true;
    }
}
