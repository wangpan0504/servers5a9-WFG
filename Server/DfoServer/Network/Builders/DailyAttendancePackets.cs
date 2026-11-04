using DfoServer.Game.Events;
using DfoServer.GameWorld.OnlineEvents;

namespace DfoServer.Network.Builders;

internal static class DailyAttendancePackets
{
    // V31 0124FFE0读event:u32、mode:u8和32B；0125060F仅将前五个u32交给819窗口。
    public static byte[] State(DailyAttendanceSnapshot snapshot, DailyAttendanceCatalog catalog)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((uint)ServerEventService.DailyAttendanceId);
        writer.Write((byte)0);
        writer.Write((uint)snapshot.Total);
        foreach (var reward in catalog.Accumulated)
            writer.Write(snapshot.Total < reward.RequiredDays ? 0u : (snapshot.Claimed & (1 << reward.Index)) != 0 ? 2u : 1u);
        writer.Write((uint)snapshot.Clears);
        writer.Write(3u);
        writer.Write(4u);
        writer.Write(5u);
        return stream.ToArray();
    }
}
