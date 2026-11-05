using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PreyTeamTcpTests
{
    static async Task LandAsync(TcpTestFixture fixture, TcpPeer peer, TownCatalog towns, RaidResources resources,
        LukeRaidClockTests.Clock clock, Dictionary<int, int> symbols)
    {
        var waiting = Cross(fixture, peer, towns, 4);
        That(waiting.All(row => row is not { Kind: 0, Opcode: 9 }), "已分配攻坚标记的单人到左侧待机不创建普通队伍");
        string tickets = fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0", peer.Id);
        var landed = await AwaitLandingAsync([peer], resources, clock, symbols);
        var received = landed.Packets[0];
        int information = received.FindIndex(row => row is { Kind: 0, Opcode: 28 });
        int map = received.FindIndex(row => row is { Kind: 0, Opcode: 29 });
        peer.Send(37, new byte[8]);
        var loaded = Read(peer, "natural landing loaded");
        Ack(loaded, 37, [1], "原37确认自然安排的真实正式副本");
        That(loaded.Any(row => row is { Kind: 0, Opcode: 30 }), "自然入图继续原共享加载屏障");
        That(received.Concat(loaded).Any(row => row is { Kind: 1, Opcode: 1361 } && row.Body[1] == 2 &&
            RosterLocations(row.Body).GetValueOrDefault(peer.Id) == landed.From),
            "正式Run安装后原1361地点显示实际到达战略symbol键，不继续显示左侧待机区");
        string run = fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", peer.Id);
        clock.Advance(1);
        var repeated = Read(peer, "natural landing same stop");
        That(repeated.All(row => row is not { Kind: 0, Opcode: 27 or 28 or 29 }) &&
            run == fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", peer.Id) &&
            tickets == fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0", peer.Id) &&
            fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3") is [[0L]],
            "同次到达及船体更新不重复建图、不重扣票，不伪造通关或奖励");
        var packets = new List<WirePacket>();
        var report = new AntonTcpProbe.AntonProbeReport { Observe = packets.Add };
        AntonTcpProbe.Note(report, received.Concat(loaded).Concat(repeated).ToList());
        FinishLoadedDungeon(peer, resources.EntryCatalog!, landed.Dungeon, received[information].Body, received[map].Body, report);
        That(report.Opcode115 == 1 && report.Opcode31 == 1 && packets.Any(row => row is { Kind: 0, Opcode: 38 }) &&
            fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[59L, 3L]] &&
            tickets == fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0", peer.Id),
            "已分配攻坚标记的单人通过独立正式Run完成清图退场，不创建Party或重复扣票");
        That(waiting.Concat(packets).All(row => row is not { Kind: 0, Opcode: 9 }),
            "单人待机、自然入图、加载、逐房清图与退场全程不发送普通队伍名册");
    }

    static async Task<(int From, int Dungeon, List<WirePacket>[] Packets)> AwaitLandingAsync(TcpPeer[] peers,
        RaidResources resources, LukeRaidClockTests.Clock clock, Dictionary<int, int> symbols, byte side = 4)
    {
        int from = symbols[resources.Script.Symbols["[slenikon from area]"]];
        int speed = symbols[resources.Script.Symbols["[slenikon speed]"]];
        var received = peers.Select(_ => new List<WirePacket>()).ToArray();
        int expected = 0;
        for (int stop = 0; stop < 16 && expected == 0; stop++)
        {
            from = from >= 316 ? 301 : from + 1;
            if (side == 5)
                for (int second = 0; second < speed; second++) clock.Advance(1);
            else clock.Advance(speed);
            ReadAll("natural landing stop " + from);
            if (received[0].LastOrDefault(row => row is { Kind: 0, Opcode: 583 }) is { } latest)
                symbols = Symbols(latest.Body);
            int dungeon = symbols[resources.Script.Symbols[$"[area_{from - 300}]"]];
            var target = resources.Script.Phases[0].Dungeons.SingleOrDefault(row => row.Id == dungeon);
            bool accepts = target is not null && target.Declarations.Any(field =>
                field.Tag == "[type]" && field.Values is [{ Text: "slenikon defence" }]) == (side == 5);
            if (!accepts)
            {
                That(received.All(rows => rows.All(row => row is not { Kind: 0, Opcode: 28 or 29 })),
                    "当前待机侧遇空据点或不匹配类型时不安排错误副本");
                continue;
            }
            expected = dungeon;
            for (int attempt = 0; attempt < 100 && received.Any(rows => !rows.Any(row => row is { Kind: 0, Opcode: 29 })); attempt++)
            {
                await Task.Delay(10);
                ReadAll("natural landing initialized");
            }
        }
        foreach (var rows in received)
        {
            int opened = rows.FindIndex(row => row is { Kind: 1, Opcode: 15, Body: [1] });
            int selection = rows.FindIndex(row => row is { Kind: 0, Opcode: 27 });
            int information = rows.FindIndex(row => row is { Kind: 0, Opcode: 28 });
            int map = rows.FindIndex(row => row is { Kind: 0, Opcode: 29 });
            That(expected > 0 && opened >= 0 && selection > opened && information > selection && map > information &&
                U32(rows[information].Body) == expected && rows.Count(row => row is { Kind: 0, Opcode: 28 }) == 1 &&
                rows.Count(row => row is { Kind: 0, Opcode: 29 }) == 1 && rows.All(row => row is not { Kind: 1, Opcode: 16 }),
                "只发送真实36待机，移动时钟到点后按15→27→28/29各建立一次正式Run，无客户端15/16请求");
        }
        return (from, expected, received);

        void ReadAll(string action)
        {
            for (int index = 0; index < peers.Length; index++) received[index].AddRange(Read(peers[index], action));
        }
    }
}
