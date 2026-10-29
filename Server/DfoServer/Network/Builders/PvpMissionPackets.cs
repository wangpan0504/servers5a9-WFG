using System.Collections.Immutable;
using DfoServer.Game.Pvp;

namespace DfoServer.Network.Builders;

internal static class PvpMissionPackets
{
    internal static byte[] Daily(byte grade, ImmutableArray<PvpDailyMissionRow> rows)
    {
        if (grade is not (2 or 4) || rows.IsDefault || rows.Length > byte.MaxValue || rows.Any(row =>
            row.Grade != grade || row.Id is 0 or > 32767 || row.Current < 0 || row.Target <= 0 ||
            row.RewardKey <= 0 || row.RewardCount <= 0) || rows.Select(row => row.Id).Distinct().Count() != rows.Length)
            throw new InvalidDataException("决斗每日任务列表无效");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(grade); writer.Write((byte)rows.Length);
        foreach (var row in rows)
        {
            writer.Write(row.Id); writer.Write(row.Current); writer.Write(row.Target);
            writer.Write(row.RewardKey); writer.Write(row.RewardCount);
        }
        return stream.ToArray();
    }

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
