using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct FiendwarBossRequest(uint Operation, uint Value);

internal static class FiendwarRequests
{
    // 00D265A0：C2S1570只写上、中、下路线号；不是1554的Boss子命令。
    internal static byte SupportLane(ReadOnlySpan<byte> body) => body.Length == 1 && body[0] <= 2
        ? body[0] : throw new InvalidDataException("RAID_SITUATION_NOTICE需要一个0至2的路线字节");

    // JP 00D2AAE5、00D734C0、03ACB3D8：1554后连续写两个u32。
    // 只恢复包形；是否可操作必须由当前正式副本、阶段与事件来源校验。
    internal static FiendwarBossRequest Boss(ReadOnlySpan<byte> body)
    {
        if (body.Length != 8) throw new InvalidDataException("FIEND_WAR_BOSS_PROC需要两个u32");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body[4..]));
    }
}
