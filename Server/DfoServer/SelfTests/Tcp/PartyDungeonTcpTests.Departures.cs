using System.Buffers.Binary;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    static async Task RunDeparturesAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "party-departures"), resources: resources);
        await fixture.StartAsync();
        using (var group = new Coop(fixture, resources, "depart", new(38, 2, 1085, 249), [3145], 95, 4))
        {
            var peers = group.Peers;
            group.Join();
            // 槽1退出再补入，数组变成0、2、3、1，接任仍应遵循队伍栏槽位。
            peers[1].Send(13); group.All(peers[1]);
            peers[0].Send(10, Invite(peers[1].Id)); group.All();
            peers[1].Send(11, Response(peers[0].Id)); group.All(peers[1]);
            group.Enter(3, 0);
            ushort partyId = U16(group.LastPackets[0].Last(p => p is { Kind: 0, Opcode: 9 }).Body, 4);
            group.Load();
            peers[0].Send(42);
            var departed = group.All();
            CheckRemoved(departed[0], 42);
            foreach (var rows in departed.Skip(1)) CheckSurvivors(rows, partyId, 1, peers.Skip(1).ToArray(), [1, 2, 3]);
            CheckMenu(peers[0], peers[1], false);

            var map = group.Map;
            var info = group.Information;
            var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(3).Mazes.Single(m => m.SourceOrdinal == info[7]);
            var path = QuestStoryRouteTests.Path(maze.StaticTopology, new(map[0], map[1]), new(info[8], info[9]));
            var active = peers.Skip(1).ToArray();
            string removedGrowth = fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=" + peers[0].Id);
            foreach (var next in path.Skip(1))
            {
                Clear();
                var move = new byte[100]; move[0] = (byte)next.X; move[1] = (byte)next.Y;
                peers[1].Send(45, move); var rows = group.All(peers[1]);
                That(rows[0].All(p => p.Opcode != 29) && rows.Skip(1).All(r => r.Any(p => p is { Kind: 0, Opcode: 29 })),
                    "接任队长带剩余成员过图，退出者不再参与同步");
                map = rows[1].Single(p => p is { Kind: 0, Opcode: 29 }).Body;
                LoadActive();
            }
            Clear();
            That(fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=" + peers[0].Id) == removedGrowth,
                "退出者不再获得幸存者后续击杀经验");
            var report = DungeonSettlementProtocolTests.Report(peers.Select(p => (p.Id, (byte)99)).ToArray());
            foreach (var peer in active)
            {
                peer.Send(46, report); var rows = group.All(peer);
                That(rows[Array.IndexOf(peers, peer)].Any(p => p is { Kind: 0, Opcode: 34 }), "换队长后剩余成员可以正常结算");
                peer.Send(69); group.All(peer); peer.Send(70);
                var rights = group.All(peer)[Array.IndexOf(peers, peer)].Single(p => p is { Kind: 1, Opcode: 70 }).Body;
                int slot = Array.IndexOf(peers, peer);
                That(U16(rights, 1 + slot * 2) != ushort.MaxValue, "退出不压缩幸存者的原始翻牌席位");
            }
            peers[1].Send(72, [1, 0]); var retry = group.All(peers[1]);
            That(retry.Skip(1).All(rows => rows.Any(p => p is { Kind: 0, Opcode: 29 })) && !retry[0].Any(p => p.Opcode == 29),
                "接任队长可带原槽位幸存者再次挑战");
            map = retry[1].Single(p => p is { Kind: 0, Opcode: 29 }).Body; LoadActive();

            peers[2].Send(14, [1]); var refused = group.All(peers[2]);
            That(refused[2].Any(p => p is { Kind: 1, Opcode: 14, Body: [0, 4, 0] }), "普通队员不能踢队长");
            That(refused.All(rows => rows.All(p => p is not { Kind: 0, Opcode: 10 })), "拒绝踢人不发布强制退队通知");
            peers[1].Send(14, [2]); var kicked = group.All(peers[1]);
            CheckRemoved(kicked[2], null);
            int kickNotice = Array.FindIndex(kicked[2], p => p is { Kind: 0, Opcode: 10, Body: [2, 0] });
            int removedNotice = Array.FindIndex(kicked[2], p => p is { Kind: 0, Opcode: 9 } && p.Body[9] == 3);
            int townNotice = Array.FindIndex(kicked[2], p => p is { Kind: 0, Opcode: 24 });
            That(kickNotice >= 0 && kickNotice < removedNotice && removedNotice < townNotice &&
                kicked[2].Count(p => p is { Kind: 0, Opcode: 10 }) == 1 &&
                !kicked[2].Any(p => p is { Kind: 1, Opcode: 14 }) &&
                kicked.Where((_, index) => index != 2).All(rows => rows.All(p => p is not { Kind: 0, Opcode: 10 })),
                "普通副本只向原目标先发旧槽强制退队，再清名册回城，不伪造目标ACK或广播队友");
            That(kicked[1].Any(p => p is { Kind: 1, Opcode: 14, Body: [1] }), "队长踢人只向发起者确认对应命令");
            CheckMenu(peers[2], peers[1], false);
            peers[3].Send(13); var left = group.All(peers[3]); CheckRemoved(left[3], 13);
            That(left.All(rows => rows.All(p => p is not { Kind: 0, Opcode: 10 })), "主动离队不发布强制退队通知");
            CheckSurvivors(left[1], partyId, 1, [peers[1]], [1]);
            peers[1].Send(42); var solo = group.All(peers[1]);
            That(solo[1].Any(p => p is { Kind: 0, Opcode: 24 }) && !solo[1].Any(p => p is { Kind: 0, Opcode: 10 } ||
                p is { Kind: 0, Opcode: 9 } && p.Body[9] == 3),
                "最后一人放弃副本回城保留单人队伍");
            CheckMenu(peers[1], peers[0], true);
            peers[1].Send(13); group.All(peers[1]); CheckMenu(peers[1], peers[0], false);

            void LoadActive()
            {
                foreach (var peer in active) { peer.Send(37, new byte[8]); group.All(peer); }
            }
            void Clear()
            {
                foreach (int at in Enumerable.Range(0, map[18]).Select(i => 19 + 22 * i)
                    .Where(at => map[at + 21] == 0 && map[at + 13] != 9).OrderBy(at => map[at + 13] == 3))
                {
                    ushort key = U16(map, at + 6);
                    peers[1].Send(39, FormalDungeonTcpTests.Death(key, peers[1].Id, map[at + 12]));
                    var rows = group.All(peers[1]);
                    That(!rows[0].Any(p => p is { Kind: 0, Opcode: 38 }) && rows.Skip(1).All(r => r.Any(p => p is { Kind: 0, Opcode: 38 })),
                        "队长退出后共享死亡只向实际副本参与者发布");
                }
            }
        }

        using (var group = new Coop(fixture, resources, "cardexit", new(38, 2, 1085, 249), [3145], 95, 4))
        {
            var peers = group.Peers;
            group.Join(); group.Enter(3, 0); group.Load();
            var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(3).Mazes.Single(m => m.SourceOrdinal == group.Information[7]);
            var path = QuestStoryRouteTests.Path(maze.StaticTopology, new(group.Map[0], group.Map[1]),
                new(group.Information[8], group.Information[9]));
            foreach (var next in path.Skip(1)) { ClearRoom(); group.Move((byte)next.X, (byte)next.Y); group.Load(); }
            ClearRoom(); group.Settle();
            peers[3].Send(13);
            var left = group.All(peers[3]);
            CheckRemoved(left[3], 13);
            peers[2].Dispose();
            peers[0].Expect(0, 6);
            Drain(peers[0]); Drain(peers[1]); Drain(peers[3]);
            peers[1].Send(69); Drain(peers[1]);
            peers[1].Send(70); Drain(peers[1]);
            var updates = new List<WirePacket>();
            for (int attempt = 0; attempt < 8 && !updates.Any(p => p is { Kind: 1, Opcode: 71, Body: [1, ..] } && p.Body[5] == 1); attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                updates.AddRange(Drain(peers[0]));
            }
            That(updates.Any(p => p is { Kind: 0, Opcode: 35 } && HasCardSeat(p.Body, 1)) &&
                updates.Any(p => p is { Kind: 1, Opcode: 71, Body: [1, ..] } &&
                    DungeonSettlementProtocolTests.CardMembers(p.Body)[1].Free == 1),
                "结算中离队及断线后个人自动翻牌仍按原成员归属通知在线幸存者");
            That(!Drain(peers[3]).Any(p => p is { Kind: 1, Opcode: 71 }), "已离队成员不收到后续翻牌");
            void ClearRoom()
            {
                var map = group.Map;
                foreach (int offset in Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
                    .Where(offset => map[offset + 21] == 0 && map[offset + 13] != 9).OrderBy(offset => map[offset + 13] == 3))
                {
                    ushort key = U16(map, offset + 6);
                    peers[0].Send(39, FormalDungeonTcpTests.Death(key, peers[0].Id, map[offset + 12]));
                    That(group.All().All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 38 } && U16(packet.Body) == key)),
                        "结算离队夹具同步真实死亡");
                }
            }
        }

        using (var group = new Coop(fixture, resources, "loadexit", new(38, 2, 1085, 249), [3145], 95, 2))
        {
            group.Join(); group.Enter(3, 0);
            group.Peers[1].Send(37, new byte[8]); group.All(group.Peers[1]);
            group.Peers[0].Send(13); var rows = group.All(); CheckRemoved(rows[0], 13);
            int host = Array.FindIndex(rows[1], p => p is { Kind: 0, Opcode: 26, Body: [1] });
            int release = Array.FindIndex(rows[1], p => p is { Kind: 0, Opcode: 30 });
            That(host >= 0 && release > host, "加载中队长退队先切换主机，再释放剩余成员加载屏障");
            group.Peers[1].Send(13); CheckRemoved(group.All(group.Peers[1])[1], 13);
        }

        using (var group = new Coop(fixture, resources, "loadlost", new(38, 2, 1085, 249), [3145], 95, 2))
        {
            group.Join(); group.Enter(3, 0);
            var survivor = group.Peers[1];
            survivor.Send(37, new byte[8]); group.All(survivor);
            group.Peers[0].Dispose();
            survivor.Expect(0, 6);
            var rows = Drain(survivor);
            int host = Array.FindIndex(rows, p => p is { Kind: 0, Opcode: 26, Body: [1] });
            int release = Array.FindIndex(rows, p => p is { Kind: 0, Opcode: 30 });
            That(host >= 0 && release > host, "加载中队长断线同样先迁移主机再释放剩余成员");
            survivor.Send(13); CheckRemoved(Drain(survivor), 13);
        }

        static byte[] Invite(ushort id) => Build(w => { w.Write(id); w.Write((byte)0); w.Write(0u); w.Write(new byte[6]); });
        static byte[] Response(ushort id) => Build(w => { w.Write(id); w.Write((byte)0); w.Write(0u); });
        static void CheckRemoved(WirePacket[] rows, ushort? ack)
        {
            That(rows.Any(p => p is { Kind: 0, Opcode: 9 } && p.Body[9] == 3) && rows.Any(p => p is { Kind: 0, Opcode: 24 }),
                "单人退场实际清除本人队伍并完成回城投影");
            if (ack is { } command) That(rows.Any(p => p.Kind == 1 && p.Opcode == command && p.Body.SequenceEqual(new byte[] { 1 })), "退场返回原命令成功应答");
        }
        static void CheckSurvivors(WirePacket[] rows, ushort id, byte leader, TcpPeer[] peers, byte[] slots)
        {
            var body = rows.Last(p => p is { Kind: 0, Opcode: 9 }).Body;
            int at = 11;
            That(U16(body, 4) == id && body[9] == 2 && body[at++] == peers.Length, "幸存者保留同一队伍ID并收到成员增量");
            That(rows.All(p => p is not { Kind: 0, Opcode: 2 or 153 }), "副本离队不重装队友Actor或以城镇百分比覆盖战斗血蓝");
            for (int i = 0; i < peers.Length; i++, at += 8) That(body[at] == slots[i] && U16(body, at + 1) == peers[i].Id, "退队不压缩或重排剩余成员槽位");
            That(body[at + 2] == leader && rows.Any(p => p is { Kind: 0, Opcode: 26 } && p.Body.SequenceEqual(new byte[] { leader })),
                "补位后的队长继任按最低槽位选择且同步UDP主机");
        }
        static void CheckMenu(TcpPeer target, TcpPeer viewer, bool hasParty)
        {
            var menu = new byte[13]; BinaryPrimitives.WriteUInt16LittleEndian(menu, target.Id); menu[2] = 15;
            viewer.Send(10, menu);
            var packet = Drain(viewer).Single(p => p is { Kind: 0, Opcode: 7 });
            That((U16(packet.Body, 7) != ushort.MaxValue) == hasParty, "服务端菜单查询与真实队伍归属一致");
        }
    }
}
