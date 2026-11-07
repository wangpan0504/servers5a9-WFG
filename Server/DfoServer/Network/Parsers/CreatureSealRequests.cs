using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct CreatureSealRequest(short PetSlot, int PetItemId, short SourceSlot);

internal static class CreatureSealRequests
{
    public static CreatureSealRequest Read(ReadOnlySpan<byte> body)
    {
        // 匹配1014实包尾部08 00 2F F6 00 00 6D 00，结合原生同构发送器：
        // i16宠物槽、i32模板、i16工具槽；前13B不含可信身份。
        if (body.Length != 21) throw new InvalidDataException("宠物封印请求长度无效");
        var request = new CreatureSealRequest(BinaryPrimitives.ReadInt16LittleEndian(body[13..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[15..]), BinaryPrimitives.ReadInt16LittleEndian(body[19..]));
        if (request.PetSlot is < 0 or > 139 || request.PetItemId <= 0 || request.SourceSlot < 0)
            throw new InvalidDataException("宠物封印请求字段无效");
        return request;
    }
}
