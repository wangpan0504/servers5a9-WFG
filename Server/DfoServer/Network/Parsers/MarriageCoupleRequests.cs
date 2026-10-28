using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Marriage;

namespace DfoServer.Network.Parsers;

internal readonly record struct MarriageCommentUpdateRequest(uint ContextId, string Text);
internal readonly record struct MarriageCommentDeleteRequest(uint ContextId, uint CommentId, byte Mode);
internal readonly record struct MarriageCommentLoadRequest(uint ContextId, byte Mode);
internal readonly record struct MarriagePartnerCheckRequest(uint ContextId, uint TicketSlot);
internal readonly record struct MarriagePartnerMoveRequest(uint TicketSlot, int Town, int Area, int X, int Y);
internal readonly record struct MarriageCoupleMoveRequest(MarriageCoupleSlot Source, MarriageCoupleSlot Target);
internal readonly record struct MarriageCoupleDeleteRequest(ushort Slot, int ItemId, int Quantity);
internal readonly record struct MarriageCoupleUseItemRequest(ushort Slot, int ItemId);

internal static class MarriageCoupleRequests
{
    // 这些原生结构在网络头以外仍附带13字节前缀；内容未定义，仅按结构宽度跳过。
    const int Prefix = 13;

    public static MarriageCommentUpdateRequest CommentUpdate(ReadOnlySpan<byte> body)
    {
        if (body.Length < 8) throw Invalid();
        int length = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
        if (length is < 1 or > MarriageCoupleRules.MaxCommentBytes || body.Length != 8 + length) throw Invalid();
        try
        {
            string text = CharacterName.Encoding.GetString(body[8..]);
            if (text.Contains('\0') || text.Any(char.IsControl)) throw Invalid();
            return new(U32(body), text);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("结婚留言不是有效CP936文本", exception);
        }
    }

    public static MarriageCommentDeleteRequest CommentDelete(ReadOnlySpan<byte> body)
    {
        body = Struct(body, 9);
        if (body[8] > 1) throw Invalid();
        return new(U32(body), U32(body[4..]), body[8]);
    }

    public static MarriageCommentLoadRequest CommentLoad(ReadOnlySpan<byte> body)
    {
        body = Struct(body, 5);
        if (body[4] > 1) throw Invalid();
        return new(U32(body), body[4]);
    }

    public static MarriagePartnerCheckRequest PartnerCheck(ReadOnlySpan<byte> body)
    {
        body = Struct(body, 8);
        return new(U32(body), U32(body[4..]));
    }

    public static MarriagePartnerMoveRequest PartnerMove(ReadOnlySpan<byte> body)
    {
        body = Struct(body, 20);
        return new(U32(body), I32(body[4..]), I32(body[8..]), I32(body[12..]), I32(body[16..]));
    }

    public static bool OpenClose(ReadOnlySpan<byte> body)
    {
        body = Struct(body, 1);
        if (body[0] > 1) throw Invalid();
        return body[0] == 1;
    }

    public static MarriageCoupleMoveRequest MoveItem(ReadOnlySpan<byte> body)
    {
        body = Struct(body, 14);
        return new(Slot(body), Slot(body[7..]));
    }

    public static MarriageCoupleDeleteRequest DeleteItem(ReadOnlySpan<byte> body)
    {
        body = Struct(body, 10);
        int item = I32(body[2..]), quantity = I32(body[6..]);
        if (item <= 0 || quantity <= 0) throw Invalid();
        return new(BinaryPrimitives.ReadUInt16LittleEndian(body), item, quantity);
    }

    public static MarriageCoupleUseItemRequest UseItem(ReadOnlySpan<byte> body)
    {
        body = Struct(body, 6);
        int item = I32(body[2..]);
        if (item <= 0) throw Invalid();
        return new(BinaryPrimitives.ReadUInt16LittleEndian(body), item);
    }

    public static void Empty(ReadOnlySpan<byte> body)
    {
        if (!body.IsEmpty) throw Invalid();
    }

    static MarriageCoupleSlot Slot(ReadOnlySpan<byte> body)
    {
        int item = I32(body[3..]);
        if (body[0] > 1 || item < 0) throw Invalid();
        return new(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[1..]), item);
    }

    static ReadOnlySpan<byte> Struct(ReadOnlySpan<byte> body, int fields)
    {
        if (body.Length != Prefix + fields) throw Invalid();
        return body[Prefix..];
    }

    static uint U32(ReadOnlySpan<byte> body) => BinaryPrimitives.ReadUInt32LittleEndian(body);
    static int I32(ReadOnlySpan<byte> body) => BinaryPrimitives.ReadInt32LittleEndian(body);
    static InvalidDataException Invalid() => new("结婚系统请求结构无效");
}
