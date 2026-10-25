using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonEnterCountTests
{
    public static void RunCore()
    {
        int before = Count;
        string Resolve(int id) => id switch { 1 => "[dungeon incount info]", 2 => "[/dungeon incount info]", _ => "[other]" };
        PvfToken[] Section(params int[] values) => [new(3, 1), .. values.Select(value => new PvfToken(0, value)), new(3, 2)];
        var table = DungeonEnterCountCatalog.Read(Section(0, 1, 11006, 3, 1, -1, 0, 1, 4000, 1, 0, -1,
            0, 2, 100, 101, 5, 0, 10), Resolve);
        That(table.Limits.Count == 4 && table.Limits[11006] == new DungeonEnterCountLimit(0, 3, 1, -1, 1) &&
            table.Limits[4000].Daily == 1 && table.Limits[100] == table.Limits[101] && table.Limits[100].GroupSize == 2,
            "角色次数目录保留单图、多图、未知标志及日周原值，不把分组自动解释为共享扣次");
        foreach (var tokens in new[] { Section(), Section(0), Section(0, 0, 3, 1, -1), Section(0, 2, 4000, 1, 0, -1),
            Section(0, 1, 4000, 256, 0, -1), Section(0, 1, 4000, 1, 0, -2), Section(0, 1, -1, 1, 0, -1),
            Section(0, 1, 4000, 1, 0, -1, 0, 1, 4000, 1, 0, -1),
            Section(0, 1, 4000, 1, 0, -1)[..^1],
            Section(0, 1, 4000, 1, 0, -1).Concat(Section(0, 1, 11006, 3, 1, -1)).ToArray(),
            new PvfToken[] { new(3, 1), new(6, 7), new(3, 2) } })
            Reject<InvalidDataException>(() => DungeonEnterCountCatalog.Read(tokens, Resolve), "损坏、重复、越界或不闭合次数表拒绝");
        Bytes(DungeonEnterCountPackets.Remaining(new(11006, 3, 0)), [0xFE, 0x2A, 0, 0, 3, 0],
            "550没有列表长度：u32地下城、u8每日剩余、u8每周剩余");
        Bytes(DungeonEnterCountPackets.Remaining(new(11007, 0, 0)), [0xFF, 0x2A, 0, 0, 0, 0],
            "次数用尽仍发送零条目覆盖客户端旧值");
        Reject<InvalidDataException>(() => DungeonEnterCountPackets.Remaining(new(0, 1, 0)), "无地下城身份不能构包");
        Console.WriteLine($"PASS {Count - before} 副本次数目录和550字节合同断言");
    }
}
