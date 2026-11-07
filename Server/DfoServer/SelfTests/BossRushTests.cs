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
        That(BossRushPackets.Timeout(120).SequenceEqual(new byte[] { 120, 0, 0, 0, 1, 0, 0, 0 }),
            "1486 独立下发秒数与原生超时类型，启动本地计时器");
        var info = BossRushPackets.DungeonInfo([new(109007051, 2), new(0, 0)]);
        That(info.Length == 22 && BinaryPrimitives.ReadInt32LittleEndian(info) == 2 &&
            BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(4)) == 109007051 &&
            info[8] == 2 && info.AsSpan(9, 4).ToArray().All(value => value == 0) &&
            info.AsSpan(13, 9).ToArray().All(value => value == 0),
            "1488 每格 9B：怪物编号、灵魂类别、四字节状态，空格保留零记录");
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
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        int offset = 0;
        var assigned = new List<int>();
        var plans = new List<DungeonMonsterPlan>();
        foreach (var map in dungeons.Get(337).Mazes.Single().MapSpecification!)
        {
            var room = rooms.Get(map, requirePassiveDefinitions:false);
            var replaced = rooms.WithBossRushMonsters(room,catalog,offset);
            var plan = DungeonMonsterPlan.FromRoom(replaced,ushort.MaxValue,1,95);
            plans.Add(plan);
            That(room.Monsters.All(monster => monster.MonsterId == 1) &&
                replaced.Monsters.Select(monster => monster.RawValues[0]).SequenceEqual(plan.Monsters.Where(monster => monster.Source is not null).Select(monster => monster.MonsterId)),
                "337替换同步源行和计划模板，原始共享房间仍保留占位怪");
            for (int i = 0; i < room.Monsters.Count; i++)
                That(replaced.Monsters[i].RawValues.Skip(1).SequenceEqual(room.Monsters[i].RawValues.Skip(1)) &&
                    replaced.Monsters[i].RankKind == room.Monsters[i].RankKind && replaced.Monsters[i].Path != room.Monsters[i].Path,
                    "替换更新真实MOB路径并保留位置、等级及品级");
            assigned.AddRange(replaced.Monsters.Select(monster => monster.MonsterId));
            offset += room.Monsters.Count;
        }
        That(assigned.SequenceEqual(catalog.Bosses),"24个战斗槽完整消费专表，无重复或遗漏，起点不消耗名单");
        var maze = dungeons.Get(337).Mazes.Single();
        var grid = rush.RoomInfo(maze, new(plans, []));
        That(grid.Length == 25 && grid[12] == default && grid.Count(row => row.MonsterId > 0) == 24,
            "原生名单按5乘5网格保留中间起点，末格怪物仍可查到");
        foreach (var plan in plans)
        {
            var point = plan.Room.Map.Position;
            var row = grid[point.Y * maze.Width + point.X];
            foreach (var monster in plan.Monsters)
                That(row.MonsterId == monster.MonsterId && catalog.SoulPoints[row.SoulType] ==
                    catalog.SoulFor(catalog.IndexOf(monster.MonsterId)), "原生坐标查怪及灵魂类别与权威计分一致");
        }
        var scored = new BossRushRunState(catalog,120000);
        That(assigned.All(scored.TryScore) && scored.Rank == 4,"替换后的全部模板可计分并推进至最高段位");
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
