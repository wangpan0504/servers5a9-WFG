using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Parsers.Inventory;

internal static class AmplificationRequests
{
    public static AmplificationRequest Purify(ReadOnlySpan<byte> body)
    {
        if (body.Length != 12) throw new InvalidDataException("净化请求必须为12字节");
        return Validate(new(AmplificationOperation.Purify, I16(body, 0), I32(body, 2), I16(body, 6), I32(body, 8)));
    }

    public static AmplificationRequest Invest(ReadOnlySpan<byte> body)
    {
        if (body.Length < 14 || body[0] > 2 || body[13] > 4)
            throw new InvalidDataException("异次元属性请求模式或固定正文无效");
        ReadOnlyMemory<byte> text = default;
        if (body[0] == 2)
        {
            if (body.Length < 18) throw new InvalidDataException("黄金增幅书请求缺少名称长度");
            int length = I32(body, 14);
            if (length is < 0 or > 256 || body.Length != 18 + length)
                throw new InvalidDataException("黄金增幅书名称长度无效");
            text = body[18..].ToArray();
        }
        else if (body.Length != 14)
            throw new InvalidDataException("赋予或扭转请求存在多余正文");
        return Validate(new((AmplificationOperation)body[0], I16(body, 1), I32(body, 3), I16(body, 7), I32(body, 9), body[13], text));
    }

    static AmplificationRequest Validate(AmplificationRequest request)
    {
        if (request.TargetSlot < 0 || request.MaterialSlot < 0 || request.TargetItemId <= 0 || request.MaterialItemId <= 0)
            throw new InvalidDataException("异次元属性请求物品身份无效");
        return request;
    }

    static short I16(ReadOnlySpan<byte> body, int at) => BinaryPrimitives.ReadInt16LittleEndian(body[at..]);
    static int I32(ReadOnlySpan<byte> body, int at) => BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
}
