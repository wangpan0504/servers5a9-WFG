using System.Buffers.Binary;
using System.Text;

namespace DfoServer.Network.Parsers;

internal enum LukeSceneReservedStatus { Malformed, UnsupportedVersion, ReservedRejected }

// 仅预留本服机器命名空间、防止落普通聊天；不是能力协商/观察回执入口，也不授权发送。
internal static class LukeSceneReservedRequests
{
    internal const int MaxTextBytes = 128;
    static ReadOnlySpan<byte> Root => "#cp-luke-scene"u8;
    static ReadOnlySpan<byte> ReceiptPrefix => "#cp-luke-scene/1 "u8;

    internal static bool IsReserved(ReadOnlySpan<byte> body)
    {
        if (body.Length < ChatRequests.HeaderLength + Root.Length) return false;
        var text = body[ChatRequests.HeaderLength..];
        return text.StartsWith(Root) && (text.Length == Root.Length || text[Root.Length] == '/' ||
            text[Root.Length] <= 0x20 || text[Root.Length] == 0x7f);
    }

    // 从原包有限前缀分类，不依赖宽松CP936解码或伪造的textLen。所有频道/具名目标同样隔离。
    // 非保留的普通近似文字保持原行为；匹配后任何畸形字段都不得重新进入普通聊天。
    internal static bool TryRead(ReadOnlySpan<byte> body, out LukeSceneReservedStatus status, out string? requestId)
    {
        status = LukeSceneReservedStatus.Malformed;
        requestId = null;
        if (!IsReserved(body)) return false;
        var text = body[ChatRequests.HeaderLength..];
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(body[7..]);
        if (declared > MaxTextBytes || declared != text.Length || body[0] != 3 ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[3..]) != 0) return true;
        if (text.Length > Root.Length && text[Root.Length] == '/' && !text.StartsWith(ReceiptPrefix))
        { status = LukeSceneReservedStatus.UnsupportedVersion; return true; }
        if (text.Length != ReceiptPrefix.Length + 32 || !text.StartsWith(ReceiptPrefix)) return true;
        var id = text[ReceiptPrefix.Length..];
        bool nonzero = false;
        foreach (byte value in id)
        {
            if (value is not (>= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f')) return true;
            nonzero |= value != '0';
        }
        if (!nonzero) return true;
        requestId = Encoding.ASCII.GetString(id);
        status = LukeSceneReservedStatus.ReservedRejected;
        return true;
    }
}
