using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class CubeContractRequests
{
    public static CubeContractState Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 2) throw new InvalidDataException("晶体契约请求必须为两个字节");
        var result = new CubeContractState(body[0], unchecked((sbyte)body[1]));
        result.Validate();
        return result;
    }
}
