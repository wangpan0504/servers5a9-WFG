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
        string[] tags = ["[daily open chance]", "[dungeon]", "[index]", "[/dungeon]"];
        PvfToken[] particular = [new(3, 0), new(0, 3), new(3, 1), new(3, 2), new(0, 316), new(3, 3),
            new(3, 1), new(3, 2), new(0, 530), new(3, 3)];
        var particularRow = DungeonIncountCatalog.ReadParticularHell(particular, index => tags[index]);
        That(particularRow.Dungeons.SequenceEqual([316]) && particularRow.Limit == 3 && particularRow.WeeklyLimit == 0 &&
            !particularRow.HasCountNotification && DungeonIncountPolicy.CanEnter(particularRow, new(2, 20)) &&
            !DungeonIncountPolicy.CanEnter(particularRow, new(3, 3)) && !DungeonIncountPolicy.Views(particularRow, default).Any(),
            "魔界专表仅316按每日3次准入、无周限额，专用832/833不冒用550，530保持自身合同");
        foreach (int limit in new[] { 0, -1, 256 })
            Reject<InvalidDataException>(() => DungeonIncountCatalog.ReadParticularHell(
                [particular[0], new(0, limit), .. particular.Skip(2)], index => tags[index]), "魔界每日次数必须为正且能完整投影");
        foreach (PvfToken[] invalid in new[]
        {
            particular.Skip(2).ToArray(),
            particular.Take(2).Concat(particular).ToArray(),
            particular.Where((_, index) => index is < 2 or > 5).ToArray(),
            particular.Concat(particular.Skip(2).Take(4)).ToArray(),
            particular[..^1]
        })
            Reject<InvalidDataException>(() => DungeonIncountCatalog.ReadParticularHell(invalid, index => tags[index]),
                "魔界缺次数、重复次数、缺316、重复316或残缺块均不免费放行");
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
        foreach (uint used in new uint[] { 0, 1, 2, 3 })
        {
            var single = RiftDailyPackets.ParticularHellDungeon(316, used);
            That(single.Length == 8 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(single) == 316 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(single.AsSpan(4)) == used &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(RiftDailyPackets.ParticularHell(3)) - used == 3 - used,
                "魔界832总额度减833已用0..3依次显示3/3、2/3、1/3、0/3，833恰好两个DWORD");
        }
        foreach (uint used in new uint[] { 0, 1, 2 })
        {
            var daybreak = RiftDailyPackets.Daybreak(2, used);
            uint total = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(daybreak);
            uint consumed = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(daybreak.AsSpan(16));
            That(daybreak.Length == 8 + RiftDailyPackets.DaybreakTailBytes && total == 2 && consumed == used &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(daybreak.AsSpan(4)) == 260 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(daybreak.AsSpan(8)) == 0 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(daybreak.AsSpan(12)) == 530 &&
                daybreak.AsSpan(20).ToArray().All(value => value == 0) && total - consumed == 2 - used,
                "黎明1545与原生读取及减法一致，按已用0/1/2分别显示2/2、1/2、0/2");
        }
        Console.WriteLine("rift-daily count checks passed particular-hell limit 3 tail 0x104 daybreak total-minus-used");
    }

    public static void RunResources(PvfArchive pvf)
    {
        var hell = pvf.ReadText("etc/particularhellparty.etc");
        var daybreak = pvf.ReadText("etc/crackofdaybreak.etc");
        That(hell.Contains("[daily open chance]\n3", StringComparison.Ordinal) ||
            hell.Contains("[daily open chance]\r\n3", StringComparison.Ordinal),
            "particularhellparty.etc 的每日开放次数是 3");
        var counts = DungeonIncountCatalog.Load(pvf);
        That(counts.ParticularHellRow is { CountKey: 316, Limit: 3, WeeklyLimit: 0, HasCountNotification: false } &&
            ReferenceEquals(counts.ParticularHellRow, counts.Find(316)) && counts.FindAccount(316) is null &&
            !counts.ChargesOnClear(316) && counts.Find(530) is { Limit: 2 },
            "真实资源魔界316接入角色入场记次，不混入账号次数、通关扣次或黎明额度");
        That(daybreak.Contains("[daily enter count]\n2", StringComparison.Ordinal) ||
            daybreak.Contains("[daily enter count]\r\n2", StringComparison.Ordinal),
            "crackofdaybreak.etc 的每日进入次数是 2");
        Console.WriteLine("rift-daily count checks passed particular-hell file chance 3 daybreak file count 2");
    }
}
