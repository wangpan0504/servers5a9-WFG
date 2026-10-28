using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Characters;

namespace DfoServer.Network.Parsers;

internal readonly record struct MarriageWeddingRequest(uint Target, byte Operation, byte ExtraA, byte ExtraB);
internal readonly record struct MarriageWeddingResponse(uint Target, byte Operation, ushort Reserved, byte Response);
internal readonly record struct MarriageMoneyGiftRequest(uint Context, uint Gold, string Text);
internal readonly record struct MarriageRingRequest(uint ItemSlot, byte NewLevel);
internal readonly record struct MarriageLetterRequest(byte Option, uint Target, ushort TargetTempId, byte TargetType);
internal readonly record struct MarriageEnterCeremonyRequest(uint Context, uint Destination, byte Flag);

internal static class MarriageWeddingRequests
{
    // 本版结构路径把栈上完整结构复制进正文，前13字节不能再次当作外层网络头剥掉。
    const int PrefixLength = 13;

    public static MarriageWeddingRequest Request(ReadOnlySpan<byte> body)
    {
        var data = Structure(body, 7);
        return new(U32(data), data[4], data[5], data[6]);
    }

    public static MarriageWeddingResponse Response(ReadOnlySpan<byte> body)
    {
        var data = Structure(body, 8);
        return new(U32(data), data[4], BinaryPrimitives.ReadUInt16LittleEndian(data[5..]), data[7]);
    }

    public static MarriageMoneyGiftRequest MoneyGift(ReadOnlySpan<byte> body)
    {
        if (body.Length < 12) throw new InvalidDataException("婚礼礼金正文过短");
        uint length = U32(body[8..]);
        if (length > int.MaxValue || body.Length - 12 != (int)length)
            throw new InvalidDataException("婚礼礼金留言长度错误");
        try { return new(U32(body), U32(body[4..]), CharacterName.Encoding.GetString(body[12..])); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("婚礼礼金留言编码错误", ex); }
    }

    public static uint Character(ReadOnlySpan<byte> body) => U32(Structure(body, 4));

    public static MarriageRingRequest UpgradeRing(ReadOnlySpan<byte> body)
    {
        var data = Structure(body, 5);
        return new(U32(data), data[4]);
    }

    public static uint UseLovePointItem(ReadOnlySpan<byte> body) => U32(Structure(body, 4));

    public static MarriageLetterRequest SendLetter(ReadOnlySpan<byte> body)
    {
        var data = Structure(body, 8);
        return new(data[0], U32(data[1..]), BinaryPrimitives.ReadUInt16LittleEndian(data[5..]), data[7]);
    }

    public static MarriageEnterCeremonyRequest EnterCeremony(ReadOnlySpan<byte> body)
    {
        var data = Structure(body, 9);
        return new(U32(data), U32(data[4..]), data[8]);
    }

    static ReadOnlySpan<byte> Structure(ReadOnlySpan<byte> body, int length)
    {
        if (body.Length != PrefixLength + length) throw new InvalidDataException("婚礼结构正文宽度错误");
        return body[PrefixLength..];
    }

    static uint U32(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32LittleEndian(data);
}
