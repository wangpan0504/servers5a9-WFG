using DfoServer.Network.Builders;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class RiftDailyPacketTests
{
    public static void Run()
    {
        var row = DungeonIncountCatalog.ReadDaybreak("[daily enter count]\n2\n[dungeon list]\n530\n[/dungeon list]").Single();
        That(row.CountKey == 530 && row.Limit == 2 && row.HasCountNotification &&
            DungeonIncountPolicy.CanEnter(row, new(1, 1)) && !DungeonIncountPolicy.CanEnter(row, new(2, 2)) &&
            DungeonIncountPolicy.Views(row, default).Single() == new DungeonEnterCount(530, 2, 2),
            "黎明专表按两次准入，并通过 1545/550 投影真实剩余");
        foreach (string text in new[] { "[dungeon list]\n530\n[/dungeon list]", "[daily enter count]\n0\n[dungeon list]\n530\n[/dungeon list]",
            "[daily enter count]\n2\n[dungeon list]\n530 530\n[/dungeon list]", "[daily enter count]\n2\n[dungeon list]\n0\n[/dungeon list]" })
            Reject<InvalidDataException>(() => DungeonIncountCatalog.ReadDaybreak(text), "黎明缺额度、零额度、重复或非法副本不放行");
        var hell = RiftDailyPackets.ParticularHell();
        That(hell.Length == 4 + RiftDailyPackets.ParticularHellTailBytes &&
            hell[0] == RiftDailyPackets.ParticularHellLimit && hell.Skip(1).All(b => b == 0),
            "魔界裂缝次数包是上限 u32 加 0x104 个原读者仍会读走的字节");
        var daybreak = RiftDailyPackets.Daybreak(1, RiftDailyPackets.DaybreakLimit);
        That(daybreak.Length == 8 + RiftDailyPackets.DaybreakTailBytes &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(daybreak) == 1 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(daybreak.AsSpan(4)) == RiftDailyPackets.DaybreakTailBytes &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(daybreak.AsSpan(8)) == 0 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(daybreak.AsSpan(12)) == RiftDailyPackets.DaybreakDungeonId &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(daybreak.AsSpan(16)) == RiftDailyPackets.DaybreakLimit &&
            daybreak.AsSpan(20).ToArray().All(value => value == 0),
            "黎明次数包同时携带当前剩余与530/2上限映射");
        Console.WriteLine("rift-daily count checks passed particular-hell limit 3 tail 0x104 daybreak remaining+530/2");
    }

    public static void RunResources(PvfArchive pvf)
    {
        var hell = pvf.ReadText("etc/particularhellparty.etc");
        var daybreak = pvf.ReadText("etc/crackofdaybreak.etc");
        That(hell.Contains("[daily open chance]\n3", StringComparison.Ordinal) ||
            hell.Contains("[daily open chance]\r\n3", StringComparison.Ordinal),
            "particularhellparty.etc 的每日开放次数是 3");
        That(daybreak.Contains("[daily enter count]\n2", StringComparison.Ordinal) ||
            daybreak.Contains("[daily enter count]\r\n2", StringComparison.Ordinal),
            "crackofdaybreak.etc 的每日进入次数是 2");
        Console.WriteLine("rift-daily count checks passed particular-hell file chance 3 daybreak file count 2");
    }
}
