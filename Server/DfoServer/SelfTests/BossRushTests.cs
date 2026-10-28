using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BossRushTests
{
    public static void RunCore()
    {
        int checks = Count;
        byte[] data = BossRushPackets.Data(2, 40, 120000);
        That(data.SequenceEqual(new byte[] { 2, 40, 0, 0, 0, 0xC0, 0xD4, 0x01, 0 }),
            "1487 为 u8 rank + i32 score + i32 剩余毫秒");
        var info = BossRushPackets.DungeonInfo([109007051, 109007052]);
        That(info.Length == 22 && BinaryPrimitives.ReadInt32LittleEndian(info) == 2 &&
            BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(4)) == 109007051 &&
            info.AsSpan(8, 5).ToArray().All(value => value == 0) &&
            BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(13)) == 109007052,
            "1488 每条 9B：怪物编号后 5 字节为零");
        That(BossRushPackets.Result(0, 0).SequenceEqual(new byte[] { 1, 0, 0, 0, 0, 0, 0 }),
            "1489 无奖励时仍占首字节 1 和零条数");
        byte[] rewarded = BossRushPackets.Result(4, 100, 10309093, 1);
        That(rewarded.Length == 15 && rewarded[0] == 1 && rewarded[1] == 4 &&
            BinaryPrimitives.ReadInt32LittleEndian(rewarded.AsSpan(2)) == 100 && rewarded[6] == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(rewarded.AsSpan(7)) == 10309093 &&
            BinaryPrimitives.ReadInt32LittleEndian(rewarded.AsSpan(11)) == 1,
            "1489 段位物品为一条 itemId/qty");
        That(DungeonSettlementRequestsParse(), "CMD 1527 只接受两个 i32");
        Console.WriteLine($"PASS {Count - checks} BossRush 1487/1488/1489 与 1527 线格式");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = new BossRushCatalog(pvf);
        That(catalog.Bosses.Length == 24 && catalog.Bosses[0] == 109007051 && catalog.Bosses[12] == 109007064 &&
            catalog.BossTypeCount == 4 && catalog.NamedTypeCount == 8 &&
            catalog.SoulPoints.SequenceEqual([5, 10, 15]) && catalog.RankThresholds.SequenceEqual([20, 40, 65, 100]),
            "BossRushDungeonInfo 仍是 24 怪、4/8 分级与 5/10/15 灵魂点");
        That(catalog.SoulFor(0) == 15 && catalog.SoulFor(3) == 15 && catalog.SoulFor(4) == 10 &&
            catalog.SoulFor(11) == 10 && catalog.SoulFor(12) == 5 && catalog.SoulFor(23) == 5,
            "名单前 4 只 15 点、随后 8 只 10 点、其余 5 点");
        That(catalog.RankOf(19) == 0 && catalog.RankOf(20) == 1 && catalog.RankOf(39) == 1 &&
            catalog.RankOf(40) == 2 && catalog.RankOf(65) == 3 && catalog.RankOf(99) == 3 && catalog.RankOf(100) == 4,
            "段位按 20/40/65/100 阈值，低于 20 为 0");
        var first = catalog.Roll(1, _ => 0);
        That(first is { Rank: 1, ItemId: 10309100, Count: 10, Weight: 3500 }, "段位 1 抽签 0 命中首行");
        var rush = new BossRushRunState(catalog, 120000);
        That(rush.TryScore(109007051) && rush.Score == 15 && rush.Rank == 0 && !rush.TryScore(109007051),
            "同一名单怪只计一次灵魂点");
        That(rush.TryScore(109007055) && rush.Score == 25 && rush.Rank == 1, "累计跨过 20 点升到段位 1");
        That(!rush.TryScore(1) && rush.Score == 25, "名单外怪物不改分");
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        That(dungeons.Get(337).TimeoutSeconds == 120 && dungeons.BossRush.Bosses.SequenceEqual(catalog.Bosses),
            "目录懒加载与 337 时限共用同一份专表");
        Console.WriteLine($"PASS {Count - checks} BossRush 专表计分与段位抽签");
    }

    static bool DungeonSettlementRequestsParse()
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(body, 7);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 9);
        return DfoServer.Network.Parsers.DungeonSettlementRequests.TryParseBossRushReport(body, out int first, out int second) &&
            first == 7 && second == 9 &&
            !DfoServer.Network.Parsers.DungeonSettlementRequests.TryParseBossRushReport([], out _, out _) &&
            !DfoServer.Network.Parsers.DungeonSettlementRequests.TryParseBossRushReport(new byte[4], out _, out _);
    }
}
