using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Inventory;

internal static class CargoRequests
{
    public static void AccountUpgrade(ReadOnlySpan<byte> body)
    {
        // 03948BC0 / 039499D2：305、306 只发送命令头。
        if (!body.IsEmpty)
            throw new InvalidDataException("Client账号仓库升级请求宽度");
    }
    public static int Gold(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4)
            throw new InvalidDataException("Client仓库金币请求宽度");
        return BinaryPrimitives.ReadInt32LittleEndian(body);
    }
    public static short PersonalUpgrade(ReadOnlySpan<byte> body)
    {
        // 02E55FC0 先按工具模板查槽，再在02E56016写入u16槽号。
        if (body.Length != 2)
            throw new InvalidDataException("Client个人仓库升级请求宽度");
        return BinaryPrimitives.ReadInt16LittleEndian(body);
    }
}
