using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.PartyDungeonTcpTests;

namespace DfoServer.Testing;

internal static class EvildomWarTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        EvildomWarPacketTests.RunCore();
        var catalog = EvildomWarCatalog.Load(resources.Archive, SelfTestCatalogs.Dungeons(resources.Archive));
        That(catalog.Dungeons.Length == 15 && catalog.MemberLimits.OrderBy(row => row.Key).Select(row => row.Value).SequenceEqual([4, 4, 1]) &&
            catalog.IncountDungeon == 5428 && catalog.NeedFatigue == 30 && catalog.DailyLimit == 1 && catalog.WeeklyLimit == 2,
            "魔界大战准入读取自身PVF，不借用400000016、raid成员数或8672任务");
        var academy = SelfTestCatalogs.Dungeons(resources.Archive).Get(5424);
        That(academy.Mazes is [var academyMaze] && academyMaze.Width == 3 && academyMaze.Height == 2 &&
            academyMaze.MapSpecification!.Select(map => map.Id).SequenceEqual([500095, 500096, 500097, 500098]),
            "5424顶层完整布局兼容解析，尺寸与四张地图均保持真实资源");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "evildom-war"), resources: resources, gm: true);
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out var fatigueMode) &&
            fatigueMode == "OK {\"infiniteFatigue\":false}", "魔界大战资格与扣费回归显式关闭开发无限疲劳");
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        int sequence = 0;
        foreach (string scenario in new[] { "no-party", "short-party", "wrong-kind", "wrong-mode", "not-leader", "fatigue", "daily", "weekly", "rollback", "hard", "normal", "guide" })
        {
            byte mode = scenario == "normal" ? (byte)0 : scenario == "guide" ? (byte)2 : (byte)1;
            int count = scenario == "guide" ? 1 : scenario == "short-party" ? 2 : 4;
            var peers = new List<TcpPeer>();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    string account = "evil" + sequence++;
                    ushort slot;
                    using (var owner = new AccountOwners().Acquire(account, () => { }))
                    {
                        var candidate = CharacterCreation.Prepare("War" + sequence, resources.Characters.Get(0),
                            catalog.WaitingArea, catalog.WaitingArea, resources.Learning.Get);
                        var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                        { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
                        var role = new CharacterRepository(fixture.Database).Create(owner, candidate with
                        { Level = 95, AcceptedStoryLevel = 95, InitialLearning = learning });
                        slot = role.Slot;
                        if (i == count - 1 && scenario == "fatigue")
                            fixture.Sql("UPDATE character_fatigue SET remaining=29,used=maximum-29 WHERE character_id=$p0", role.Id);
                        if (i == count - 1 && scenario is "daily" or "weekly")
                        {
                            var period = DungeonIncountPolicy.Period(fixture.UtcNow);
                            fixture.Sql("INSERT INTO character_dungeon_incount VALUES($p0,$p1,5428,$p2,$p3,$p4,$p5)",
                                role.Id, account, period.Day, scenario == "daily" ? 1 : 0, period.Week, scenario == "weekly" ? 2 : 0);
                        }
                    }
                    var peer = Open(fixture, account, gamePort: endpoints[41], channelNumber: 41);
                    peers.Add(peer);
                    peer.Select(slot, prepared: true, resumeTown: true, room: false);
                    That(peer.LoginEndpoint[3] == 54 && fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[67L, 2L]],
                        "真实type54频道恢复67/2等待区");
                    peer.Send(2, Build(writer =>
                    {
                        writer.Write((byte)5); writer.Write(0x0100007fu); writer.Write(0x0100007fu);
                        writer.Write((ushort)(32000 + i)); writer.Write(512u);
                    }));
                    Drain(peer);
                }
                WirePacket[][] All(TcpPeer? caller = null)
                {
                    caller ??= peers[0];
                    var packets = new WirePacket[peers.Count][];
                    packets[peers.IndexOf(caller)] = Drain(caller);
                    foreach (var peer in peers.Where(peer => peer != caller)) packets[peers.IndexOf(peer)] = Drain(peer);
                    return packets;
                }
                All();
                var leader = peers[0];
                if (scenario != "no-party")
                {
                    leader.Send(12, Build(writer =>
                    {
                        writer.Write((byte)0); writer.Write((byte)0); writer.Write(3); writer.Write("war"u8);
                        writer.Write((byte)4); writer.Write(0u); writer.Write((byte)5); writer.Write((ushort)0); writer.Write((byte)0);
                        writer.Write(scenario == "wrong-kind" ? (byte)0 : (byte)5);
                        writer.Write(scenario == "wrong-mode" ? (ushort)3 : (ushort)mode); writer.Write((byte)7);
                    }));
                    That(All()[0].Any(packet => packet is { Kind: 0, Opcode: 9 }), "CMD12成功以真实NOTI9名册确认建队");
                    foreach (var peer in peers.Skip(1))
                    {
                        leader.Send(10, Build(writer => { writer.Write(peer.Id); writer.Write((byte)0); writer.Write(0u); writer.Write(new byte[6]); }));
                        All();
                        peer.Send(11, Build(writer => { writer.Write(leader.Id); writer.Write((byte)0); writer.Write(0u); }));
                        That(All(peer).Take(peers.IndexOf(peer) + 1).All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 9 })), "所有队员真实加入同一Party");
                    }
                }
                string assets = fixture.Snapshot();
                leader.Send(15, BitConverter.GetBytes(5426u));
                That(All()[0].Any(packet => packet is { Kind: 1, Opcode: 15 } && packet.Body[0] == 0) && fixture.Snapshot() == assets,
                    "开战前的15不能绕过专用准入");
                var requester = scenario == "not-leader" ? peers[1] : leader;
                requester.Send(1652);
                var started = All(requester);
                bool accepted = scenario is "rollback" or "hard" or "normal" or "guide";
                That(started[peers.IndexOf(requester)].Any(packet => packet is { Kind: 1, Opcode: 1652 } && packet.Body.SequenceEqual(accepted ? new byte[] { 1 } : new byte[] { 0, 0, 0 })) &&
                    fixture.Snapshot() == assets, "1652资格拒绝或冻结战役，首次进图前不提前扣费: " + scenario);
                if (!accepted)
                {
                    That(started.All(rows => !rows.Any(packet => packet.Opcode is 1671 or 28 or 29)), "拒绝不发布战斗状态或进图成功");
                    continue;
                }
                int dungeon = mode == 0 ? 5420 : mode == 1 ? 5426 : 5440;
                That(started.All(rows => rows.Single(packet => packet is { Kind: 0, Opcode: 1671 }).Body is { Length: 45 } body &&
                    body[0] == 2 && body[1] == mode && U32(body, 6) == 5 && U32(body, 10) == dungeon), "1671先安装本模式五个真实PVF副本，客户端右侧面板可选");
                var info = started[0].Single(packet => packet is { Kind: 0, Opcode: 1671 }).Body;
                That(Enumerable.Range(0, 4).Select(i => info[15 + i * 7]).Order().SequenceEqual(new byte[] { 0, 1, 2, 3 }) && info[44] == 3 &&
                    started.All(rows => rows.Single(packet => packet is { Kind: 0, Opcode: 1671 }).Body.AsSpan(6).SequenceEqual(info.AsSpan(6))),
                    "四图属性只抽一次且全队一致，王座开局封锁");
                leader.Send(15, BitConverter.GetBytes((uint)(mode == 0 ? 5416 : mode == 1 ? 5422 : 5436)));
                That(All()[0].Any(packet => packet is { Kind: 1, Opcode: 15 } && packet.Body[0] == 0), "不能直接绕过两层结界进入王座");
                leader.Send(1652);
                That(All()[0].Any(packet => packet is { Kind: 1, Opcode: 1652, Body: [0, 0, 0] }), "重复开战不刷新截止时间或产生第二次计费");
                leader.Send(15, BitConverter.GetBytes((uint)dungeon));
                That(All().All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 27 })), "专用面板15为全队建立原生选图场景");
                var selection = Convert.FromHexString("030000000200000000FFFF0000000000000000000000000000");
                BinaryPrimitives.WriteUInt32LittleEndian(selection, (uint)dungeon);
                if (scenario == "rollback")
                    fixture.Sql("CREATE TRIGGER reject_war BEFORE UPDATE ON character_dungeon_state WHEN NEW.character_id=" + peers[^1].Id +
                        " BEGIN SELECT RAISE(ABORT,'war entry rollback'); END");
                assets = fixture.Snapshot();
                leader.Send(16, selection);
                if (scenario == "rollback")
                {
                    leader.Expect(0, 9); leader.Closed();
                    That(peers.Skip(1).All(peer => !Drain(peer).Any(packet => packet.Opcode is 28 or 29)) && fixture.Snapshot() == assets,
                        "后续队员SQL失败，全队次数、疲劳和尝试号原子回滚");
                    fixture.Sql("DROP TRIGGER reject_war");
                    continue;
                }
                var entered = All();
                That(entered.All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 28 }) && rows.Any(packet => packet is { Kind: 0, Opcode: 29 })) &&
                    entered.All(rows => Array.FindIndex(rows, packet => packet.Opcode == 28) < Array.FindIndex(rows, packet => packet.Opcode == 29)),
                    "普通/困难/引导均沿正式28→29创建真实地图和怪物: " + scenario);
                foreach (var peer in peers)
                    That(fixture.Query("SELECT used FROM character_fatigue WHERE character_id=$p0", peer.Id) is [[30L]] &&
                        fixture.Query("SELECT daily_used,weekly_used FROM character_dungeon_incount WHERE character_id=$p0 AND dungeon=5428", peer.Id) is [[1L, 1L]],
                        "每人一次扣30疲劳并记5428日周次数");
                leader.Send(16, selection);
                All();
                foreach (var peer in peers) { peer.Send(37, new byte[8]); All(peer); }
                That(fixture.Snapshot() != assets && peers.All(peer => fixture.Query("SELECT used FROM character_fatigue WHERE character_id=$p0", peer.Id) is [[30L]]),
                    "重复16和加载不再次收费，不按房间扣魔界大战疲劳");
            }
            finally { foreach (var peer in peers) peer.Dispose(); }
        }
        Console.WriteLine($"PASS evildom war TCP {Count - before}");
    }
}
