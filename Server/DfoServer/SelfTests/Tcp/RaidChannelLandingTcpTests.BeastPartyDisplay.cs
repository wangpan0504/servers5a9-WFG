using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class RaidChannelLandingTcpTests
{
    static async Task RunBeastPartyDisplayAsync(TcpTestFixture fixture, TcpTestResources resources,
        DailyBoosterTestClock clock, int port, Location position)
    {
        string[] accounts = ["beastdisplayalpha", "beastdisplaybeta"];
        foreach (string account in accounts)
        {
            using (var bootstrap = Open(fixture, account, gamePort: port))
            { bootstrap.Send(3, [1]); bootstrap.Expect(1, 3, [1]); bootstrap.Closed(); }
            CreateRaider(fixture, resources, account == accounts[0] ? "BeastViewA" : "BeastViewB", position, account);
            using var owner = new AccountOwners().Acquire(account, () => { });
            BeastDungeonSessionTests.GiveTickets(new CharacterRepository(fixture.Database), owner);
        }
        using var alpha = Open(fixture, accounts[0], gamePort: port);
        using var beta = Open(fixture, accounts[1], gamePort: port);
        TcpPeer[] peers = [alpha, beta];
        foreach (var peer in peers)
        {
            peer.Select(prepared: true, resumeTown: true, room: false); Drain(peer);
            peer.Send(1426); Drain(peer);
            peer.Send(2, Build(w => { w.Write((byte)5); w.Write(0x0100007fu); w.Write(0x0100007fu); w.Write((ushort)30000); w.Write(512u); }));
            Drain(peer);
        }
        foreach (var peer in peers) Drain(peer);
        byte[] Identity(ushort id, bool invite) => Build(w =>
        { w.Write(id); w.Write((byte)0); w.Write(0u); if (invite) w.Write(new byte[6]); });
        int day = BeastDungeonRotation.ServiceDay(clock.GetUtcNow());
        int dungeon = BeastDungeonRotation.Pair(day).First;
        var definition = SelfTestCatalogs.Dungeons(resources.Archive).Get(dungeon);
        var layout = definition.BeastLayouts[0];
        var maze = DungeonEntryPlan.ApplyBeastLayout(definition.Mazes[0], layout);
        var serials = peers.ToDictionary(p => p, _ => 0u);
        foreach (bool timeout in new[] { false, true })
        {
            alpha.Send(10, Identity(beta.Id, true));
            That(Drain(alpha).Any(p => p is { Kind: 1, Opcode: 10, Body: [1] }) &&
                Drain(beta).Any(p => p is { Kind: 0, Opcode: 7 }), "魔兽显示组队使用真实邀请");
            beta.Send(11, Identity(alpha.Id, false));
            var accepted = Both(beta);
            foreach (var peer in peers) That(accepted[peer].Any(p => p is { Kind: 0, Opcode: 9 }), "两端实际加入同一队伍");
            alpha.Send(15, new byte[4]);
            foreach (var peer in peers)
            {
                var opened = Drain(peer);
                That(opened.Any(p => p is { Kind: 0, Opcode: 27 }), "两端共同打开原图选图");
                That(opened.Single(p => p.Opcode == 27).Body.AsSpan(^8).SequenceEqual(
                    Build(w => { w.Write(dungeon); w.Write(BeastDungeonRotation.Pair(day).Second); })),
                    "两端原生选图均携带当日两图入侵标识");
                CheckView(peer, opened.Single(p => p.Opcode == 0x7FF1).Body, 1);
            }
            byte[] request = new byte[25];
            BinaryPrimitives.WriteInt32LittleEndian(request, dungeon);
            request[4] = 4; request[7] = 2; request[9] = 255; request[10] = 255;
            alpha.Send(16, request);
            var first = Both();
            byte[] map = first[alpha].Single(p => p.Opcode == 29).Body;
            That(first[beta].Single(p => p.Opcode == 29).Body.SequenceEqual(map), "两人安装同一真实首房");
            int completed = 0;
            var visited = new HashSet<DungeonPoint>();
            Guid? instance = null;
            bool ready = LoadAndClear();
            foreach (var target in layout.Maps.Where(row => row.Kind == "Beast"))
            {
                if (ready) break;
                var route = QuestStoryRouteTests.Path(maze.StaticTopology, new(map[0], map[1]), target.Position);
                foreach (var point in route.Skip(1))
                {
                    byte[] move = new byte[100]; move[0] = (byte)point.X; move[1] = (byte)point.Y;
                    alpha.Send(45, move); var moved = Both();
                    map = moved[alpha].Single(p => p.Opcode == 29).Body;
                    That(moved[beta].Single(p => p.Opcode == 29).Body.SequenceEqual(map), "两人共同过图不分裂阶段");
                    CheckProgress(moved);
                    ready = LoadAndClear();
                    if (ready) break;
                }
            }
            That(ready && completed == (timeout ? 2 : 6), "两人真实网络进入第三阶段或完成六阶段");
            if (timeout)
            {
                clock.Advance(TimeSpan.FromSeconds(180));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (fixture.Events().Count(row => row.GetProperty("Kind").GetString() == "formal_beast_progress" &&
                    row.GetProperty("Detail").GetProperty("View") is var view &&
                    view.GetProperty("InstanceId").GetGuid() == instance && view.GetProperty("Terminal").GetInt32() == 2) < 2)
                    await Task.Delay(20, deadline.Token);
                var expired = Both(); CheckProgress(expired, BeastTerminal.TimedOut);
                That(expired.Values.All(rows => !rows.Any(p => p.Opcode == 31)), "共同超时不冒发普通通关许可");
            }
            foreach (var peer in peers)
            {
                peer.Send(42); var departed = Both(peer);
                That(departed[peer].Any(p => p is { Kind: 1, Opcode: 42, Body: [1] }), "两端都可正常退场");
                CheckView(peer, departed[peer].Single(p => p.Opcode == 0x7FF1 && p.Body[4] == 0).Body, 0);
            }
            // 最后退场者仍保留普通单人队伍；下一轮以真实离队清理，避免重邀后队长沿用为另一人。
            beta.Send(13);
            That(Both(beta)[beta].Any(p => p is { Kind: 1, Opcode: 13, Body: [1] }), "再次组队前真实解散剩余单人队伍");

            bool LoadAndClear()
            {
                foreach (var peer in peers) { peer.Send(37, new byte[8]); CheckProgress(Both(peer)); }
                var point = new DungeonPoint(map[0], map[1]);
                if (!visited.Add(point)) return false;
                bool stage = layout.Maps.Single(row => row.Position == point).Kind == "Beast";
                if (timeout && stage && completed == 2) return true;
                int boss = 107005374 + completed;
                foreach (int offset in Enumerable.Range(0, map[18]).Select(i => 19 + i * 22)
                    .Where(at => map[at + 13] <= 3 && map[at + 21] == 0).OrderBy(at => U32(map, at + 8) == boss))
                {
                    alpha.Send(39, FormalDungeonTcpTests.Death(U16(map, offset + 6), alpha.Id, map[offset + 12]));
                    var killed = Both();
                    bool finalBoss = stage && U32(map, offset + 8) == boss;
                    CheckProgress(killed, finalBoss && completed == 5 ? BeastTerminal.Cleared : BeastTerminal.Active);
                    if (finalBoss)
                        That(killed.Values.All(rows => rows.Any(p => p.Opcode == 0x7FF1 && p.Body[7] == completed + 1)),
                            "指定Boss提交后两端共同收到已确认阶段显示");
                }
                if (stage) completed++;
                return completed == 6;
            }
            void CheckProgress(Dictionary<TcpPeer, WirePacket[]> notices, BeastTerminal? terminal = null)
            {
                var views = notices.ToDictionary(row => row.Key, row => row.Value.Where(p => p.Opcode == 0x7FF1).ToArray());
                foreach (var row in views)
                    foreach (var packet in row.Value)
                    {
                        CheckView(row.Key, packet.Body, 2);
                        Guid actual = new(packet.Body.AsSpan(16, 16));
                        That(instance is null || instance == actual, "两端所有阶段共用同一实例GUID"); instance = actual;
                        if (terminal is { } expected) That(packet.Body[5] == (byte)expected, "共同终局显示一致");
                    }
                if (views.Values.All(rows => rows.Length > 0))
                {
                    byte[] a = views[alpha][^1].Body, b = views[beta][^1].Body;
                    That(a.AsSpan(5, 3).SequenceEqual(b.AsSpan(5, 3)) && a.AsSpan(32, 96).SequenceEqual(b.AsSpan(32, 96)),
                        "两端完成层数、共享截止、分数与已获奖励逐字节一致");
                }
            }
        }
        foreach (var peer in peers)
            That(Convert.ToInt64(fixture.Query("SELECT used FROM character_beast_dungeon_entries WHERE character_id=$p0 AND dungeon_id=$p1", peer.Id, dungeon)[0][0]) == 2,
                "组队通关与超时各自只扣一次个人入场次数");

        Dictionary<TcpPeer, WirePacket[]> Both(TcpPeer? requester = null)
        {
            requester ??= alpha;
            var rows = new Dictionary<TcpPeer, WirePacket[]> { [requester] = Drain(requester) };
            foreach (var peer in peers.Where(p => !ReferenceEquals(p, requester))) rows.Add(peer, Drain(peer));
            return rows;
        }
        WirePacket[] Drain(TcpPeer peer) => PartyDungeonTcpTests.Drain(peer);
        void CheckView(TcpPeer peer, byte[] body, byte kind)
        {
            That(body.Length == 144 && U32(body) == 0x31565342 && body[4] == kind && U16(body, 8) == peer.Id &&
                U32(body, 12) > serials[peer], "组队显示绑定各自角色和严格递增连接序号");
            serials[peer] = U32(body, 12);
        }
    }
}
