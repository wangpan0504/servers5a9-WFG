using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal static class GuildRankingRequests
{
    // Actor UID只作原生查询引用；权限与所属公会始终取已绑定会话。
    public static bool TrySummary(byte[] body, out ushort actor)
    {
        actor = body.Length == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(body) : (ushort)0;
        return body.Length == 2;
    }

    public static bool TryPage(byte[] body, out byte mode, out byte sort, out ushort page)
    {
        mode = body.Length == 4 ? body[0] : (byte)0;
        sort = body.Length == 4 ? body[1] : (byte)0;
        page = body.Length == 4 ? BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2)) : (ushort)0;
        return body.Length == 4 && mode <= 1 && sort <= 7 && page > 0 && (mode == 0 || page == 1);
    }
}
