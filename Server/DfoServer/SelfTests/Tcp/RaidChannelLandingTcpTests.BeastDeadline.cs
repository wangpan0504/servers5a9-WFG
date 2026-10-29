using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class RaidChannelLandingTcpTests
{
    static async Task RunBeastNativeDeadlineAsync(TcpTestFixture fixture, TcpTestResources resources,
        DailyBoosterTestClock clock, int port, ushort slot, int dungeon)
    {
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            BeastDungeonSessionTests.GiveTickets(new CharacterRepository(fixture.Database), owner);
        using var peer = Open(fixture, gamePort: port, channelNumber: 10);
        peer.Select(slot, prepared: true, resumeTown: true, room: false);
        peer.Barrier();
        peer.Send(15, new byte[4]); peer.Expect(1, 15, [1]);
        peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]); peer.Expect(0, 27); peer.Barrier();
        byte[] request = new byte[25];
        BinaryPrimitives.WriteUInt32LittleEndian(request, checked((uint)dungeon));
        request[4] = 4; request[7] = 2; request[9] = 255; request[10] = 255;
        peer.Send(16, request); peer.Expect(0, 28);
        byte[] map = peer.Expect(0, 29).Body;
        var definition = SelfTestCatalogs.Dungeons(resources.Archive).Get(dungeon);
        var layout = definition.BeastLayouts[0];
        var maze = DungeonEntryPlan.ApplyBeastLayout(definition.Mazes[0], layout);
        int completed = 0;
        var visited = new HashSet<DungeonPoint>();
        bool firstLoading = true;
        bool ready = LoadAndClear();
        foreach (var target in layout.Maps.Where(row => row.Kind == "Beast"))
        {
            if (ready) break;
            var route = QuestStoryRouteTests.Path(maze.StaticTopology, new(map[0], map[1]), target.Position);
            foreach (var point in route.Skip(1))
            {
                byte[] move = new byte[100]; move[0] = (byte)point.X; move[1] = (byte)point.Y;
                peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
                ready = LoadAndClear();
                if (ready) break;
            }
        }
        That(ready && completed == 2, "beast-deadline:真实TCP清两阶段后抵达第三阶段并保留指定Boss");
        // 等待后台已建立该房间投影，再推进注入时钟；此后不发送任何请求触发超时。
        await WaitForProgress(2, BeastTerminal.Active);
        clock.Advance(TimeSpan.FromSeconds(180));
        await WaitForProgress(2, BeastTerminal.TimedOut);
        That(fixture.Query("SELECT count(*) FROM character_beast_stage_rewards WHERE character_id=$p0", peer.Id) is [[2L]],
            "beast-deadline:后台自动超时保留已提交的两个阶段奖励");
        string before = fixture.Snapshot("SELECT * FROM character_beast_stage_rewards WHERE character_id=$p0", peer.Id);
        int bossOffset = Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
            .Single(offset => U32(map, offset + 8) == 107005376 && map[offset + 13] == 3);
        peer.Send(39, FormalDungeonTcpTests.Death(U16(map, bossOffset + 6), peer.Id, map[bossOffset + 12]));
        peer.Send(46, DungeonSettlementProtocolTests.Report(peer.Id, 90)); peer.Barrier();
        That(fixture.Snapshot("SELECT * FROM character_beast_stage_rewards WHERE character_id=$p0", peer.Id) == before,
            "beast-deadline:超时后迟到指定Boss及普通结算不能再领阶段奖励");
        peer.Send(42); peer.Expect(1, 42, [1]);
        peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]); peer.Expect(0, 24);
        FormalDungeonTcpTests.RefreshedQuests(peer); peer.Barrier();
        That(fixture.Query("SELECT used FROM character_beast_dungeon_entries WHERE character_id=$p0", peer.Id) is [[1L]],
            "beast-deadline:超时可经原返城请求退出，已入场次数不退");
        peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();

        bool LoadAndClear()
        {
            if (firstLoading)
            {
                peer.Send(37, new byte[8]);
                var payment = peer.Expect(0, 14).Body;
                That(U32(payment, 5) == BeastDungeonTicket.Event && U32(payment, 9) == 4,
                    "首房提交后先投影真实活动票扣一张，才确认加载");
                peer.Expect(1, 37, [1]); peer.Expect(0, 30, new byte[5]);
                peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
                firstLoading = false;
            }
            else FormalDungeonTcpTests.Load(peer);
            peer.Barrier();
            var point = new DungeonPoint(map[0], map[1]);
            if (!visited.Add(point)) return false;
            bool stage = layout.Maps.Single(row => row.Position == point).Kind == "Beast";
            if (stage && completed == 2) return true;
            int boss = 107005374 + completed;
            foreach (int offset in Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
                .Where(offset => map[offset + 13] <= 3 && map[offset + 21] == 0)
                .OrderBy(offset => U32(map, offset + 8) == boss))
            {
                ushort key = U16(map, offset + 6);
                peer.Send(39, FormalDungeonTcpTests.Death(key, peer.Id, map[offset + 12]));
                That(U16(peer.Expect(0, 38).Body) == key, "原图死亡回执来自当前发布演员");
                peer.Barrier(packet => That(packet.Opcode is 37 or 14 or 115, "死亡只接收成长、库存或演员消失通知"));
            }
            if (stage) completed++;
            return false;
        }

        async Task WaitForProgress(int stage, BeastTerminal terminal)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!fixture.Events().Any(row => row.GetProperty("Kind").GetString() == "formal_beast_progress" &&
                row.GetProperty("Detail").GetProperty("View") is var view &&
                view.GetProperty("Stage").GetInt32() == stage && view.GetProperty("Terminal").GetInt32() == (int)terminal))
            {
                if (deadline.IsCancellationRequested)
                    throw new InvalidOperationException($"beast-deadline:missing automatic progress stage={stage} terminal={terminal}");
                await Task.Delay(20);
            }
        }
    }
}
