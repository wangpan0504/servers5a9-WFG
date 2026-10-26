using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class AvatarAbilityChangeRequests
{
    public static AvatarAbilityChangeRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 13) throw new InvalidDataException("装扮属性转换请求必须为13字节");
        var request = new AvatarAbilityChangeRequest(BinaryPrimitives.ReadInt16LittleEndian(body),
            BinaryPrimitives.ReadInt32LittleEndian(body[2..]), BinaryPrimitives.ReadInt16LittleEndian(body[6..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[8..]), body[12]);
        if (request.SourceSlot < 0 || request.AvatarSlot < 0 || request.SourceId <= 0 || request.AvatarId <= 0)
            throw new InvalidDataException("装扮属性转换的槽位或编号无效");
        return request;
    }
}
