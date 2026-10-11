using System.Buffers.Binary;
using DfoServer.Game.Titles;

namespace DfoServer.Network.Parsers;

internal static class TitleRequests
{
    public static ushort Other(ReadOnlySpan<byte> body)
    {
        if (body.Length != 2 || BinaryPrimitives.ReadUInt16LittleEndian(body) == 0) throw new InvalidDataException("他人称号簿请求必须为非零角色u16");
        return BinaryPrimitives.ReadUInt16LittleEndian(body);
    }
    public static TitleBookRequest Book(ReadOnlySpan<byte> body)
    {
        if (body.Length != 20) throw new InvalidDataException("称号取放包必须为五个i32");
        var request = new TitleBookRequest(BinaryPrimitives.ReadInt32LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[8..]), BinaryPrimitives.ReadInt32LittleEndian(body[12..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[16..]));
        if (request.Category is < 0 or > 4) throw new InvalidDataException("称号取放分类越界，不能回传危险数组索引");
        return request;
    }
    public static TitleTriggerRequest Trigger(ReadOnlySpan<byte> body)
    {
        if (body.Length != 10) throw new InvalidDataException("称号进度包必须为i32和三个u16");
        return new(BinaryPrimitives.ReadInt32LittleEndian(body), new(BinaryPrimitives.ReadUInt16LittleEndian(body[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[6..]), BinaryPrimitives.ReadUInt16LittleEndian(body[8..])));
    }
}
