using System.Collections.Immutable;
using DfoServer.Game.Pvp;

namespace DfoServer.Network.Builders;

internal static class PvpMissionPackets
{
    internal static byte[] List(ImmutableArray<PvpMissionRow> rows)
    {
        if (rows.IsDefault || rows.Length > byte.MaxValue || rows.Select(row => row.Id).Distinct().Count() != rows.Length ||
            rows.Any(row => row.Id is 0 or > 32767 || row.Current < 0 || row.Target <= 0))
            throw new InvalidDataException("决斗任务列表身份或有符号计数无效");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)rows.Length);
        foreach (var row in rows) { writer.Write(row.Id); writer.Write(row.Current); writer.Write(row.Target); }
        return stream.ToArray();
    }
}
