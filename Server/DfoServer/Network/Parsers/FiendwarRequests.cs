using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct FiendwarBossRequest(uint Operation, uint Value);

internal static class FiendwarRequests
{
    // JP 00D2AAE5、00D734C0、03ACB3D8：1554后连续写两个u32。
    // 只恢复包形；是否可操作必须由当前正式副本、阶段与事件来源校验。
    internal static FiendwarBossRequest Boss(ReadOnlySpan<byte> body)
    {
        if (body.Length != 8) throw new InvalidDataException("FIEND_WAR_BOSS_PROC需要两个u32");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body[4..]));
    }
}
