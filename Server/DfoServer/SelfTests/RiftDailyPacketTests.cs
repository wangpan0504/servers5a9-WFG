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
        That(row.CountKey == 530 && row.Limit == 2 && !row.HasCountNotification &&
            DungeonIncountPolicy.CanEnter(row, new(1, 1)) && !DungeonIncountPolicy.CanEnter(row, new(2, 2)) &&
            !DungeonIncountPolicy.Views(row, default).Any(), "黎明专表按两次准入，不借通用550发送未证实通知");
        foreach (string text in new[] { "[dungeon list]\n530\n[/dungeon list]", "[daily enter count]\n0\n[dungeon list]\n530\n[/dungeon list]",
            "[daily enter count]\n2\n[dungeon list]\n530 530\n[/dungeon list]", "[daily enter count]\n2\n[dungeon list]\n0\n[/dungeon list]" })
            Reject<InvalidDataException>(() => DungeonIncountCatalog.ReadDaybreak(text), "黎明缺额度、零额度、重复或非法副本不放行");
        var hell = RiftDailyPackets.ParticularHell();
        That(hell.Length == 4 + RiftDailyPackets.ParticularHellTailBytes &&
            hell[0] == RiftDailyPackets.ParticularHellLimit && hell.Skip(1).All(b => b == 0),
            "魔界裂缝次数包是上限 u32 加 0x104 个原读者仍会读走的字节");
        That(RiftDailyPackets.Daybreak().SequenceEqual(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0 }),
            "黎明裂缝次数包是上限 u32 加长度为 0 的后续字节数");
        Console.WriteLine("rift-daily count checks passed particular-hell limit 3 tail 0x104 daybreak 02 00 00 00 00 00 00 00");
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
