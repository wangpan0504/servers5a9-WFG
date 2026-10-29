using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
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
        EvildomWarTransactionTests.Run(resources, catalog, root);
        EvildomWarRewardTransactionTests.Run(resources, catalog, root);
        EvildomWarRecoveryTransactionTests.Run(resources, catalog, root);
        EvildomWarHiddenTransactionTests.Run(resources, catalog, root);
        EvildomWarGuideTests.Run(resources, catalog, root);
        That(catalog.Dungeons.Length == 15 && catalog.MemberLimits.OrderBy(row => row.Key).Select(row => row.Value).SequenceEqual([4, 4, 1]) &&
            catalog.IncountDungeon == 5428 && catalog.NeedFatigue == 30 && catalog.DailyLimit == 1 && catalog.WeeklyLimit == 2,
            "魔界大战准入读取自身PVF，不借用400000016、raid成员数或8672任务");
        That(catalog.HiddenDungeons.Select(row => (row.Mode, row.Id, row.RawWeight))
                .SequenceEqual(new[] { ((byte)0, 5421, 1000), ((byte)1, 5427, 1000), ((byte)2, 5441, 1000) }) &&
            catalog.AntiEvilLimits.OrderBy(row => row.Key).Select(row => row.Value).SequenceEqual([5000, 5150, 4000]),
            "隐藏图独立于五图面板，三模式抗魔阈值完整解析而不作为硬禁入条件");
        That(catalog.MovieTime == 74 && catalog.DisconnectWaitTime == 300 && catalog.DisconnectRetryItem == 10310974 &&
            catalog.SwordBuffRate == 15 && catalog.ShieldBuffRate == 15 && catalog.RecruitingArea == new DfoServer.GameWorld.Towns.Location(67, 1, 891, 291),
            "魔界影片、断线、剑盾及招募位置读取实际资源，解析不代表机制已生效");
        That(catalog.Rewards is [_, _, _, _, _, { GroupRaw: 3, TypeRaw: 0, ItemRaw: -1, CountRaw: 0, WeightRaw: 9372 },
                { GroupRaw: 3, TypeRaw: 1, ItemRaw: 10310788, CountRaw: 1, WeightRaw: 500 }] &&
            catalog.HardModeRewards.Length == 5 && catalog.GuideModeRewards.Where(row => row.GroupRaw == 3).Sum(row => row.WeightRaw) == 9922,
            "奖励保留原五元组、哨兵和非满额权重，困难表独立保存而不猜叠加或替代");
        var academy = SelfTestCatalogs.Dungeons(resources.Archive).Get(5424);
        That(academy.Mazes is [var academyMaze] && academyMaze.Width == 3 && academyMaze.Height == 2 &&
            academyMaze.MapSpecification!.Select(map => map.Id).SequenceEqual([500095, 500096, 500097, 500098]),
            "5424顶层完整布局兼容解析，尺寸与四张地图均保持真实资源");
        // 固定本组业务时钟，保留全队1671整包精确断言；实际发送跨秒不应改变同一战况的测试预期。
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "evildom-war"), resources: resources, gm: true,
            evildomHiddenDraw: _ => 0, clock: new Inventory.DailyBoosterTestClock(DateTimeOffset.UtcNow));
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out var fatigueMode) &&
            fatigueMode == "OK {\"infiniteFatigue\":false}", "魔界大战资格与扣费回归显式关闭开发无限疲劳");
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        int sequence = 0;
        foreach (string scenario in new[] { "training-solo", "normal-solo", "hard-solo", "hard-duo", "normal-trio", "guide-overfull", "ticket-solo",
            "no-party", "wrong-kind", "wrong-mode", "not-leader", "fatigue", "daily", "weekly", "rollback", "hard", "normal", "guide" })
        {
            byte mode = scenario is "normal" or "normal-solo" or "normal-trio" or "training-solo" ? (byte)0 :
                scenario is "guide" or "guide-overfull" ? (byte)2 : (byte)1;
            int count = scenario switch { "normal-solo" or "training-solo" or "hard-solo" or "guide" or "ticket-solo" => 1,
                "hard-duo" or "guide-overfull" => 2, "normal-trio" => 3, _ => 4 };
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
                        { Level = 95, AcceptedStoryLevel = 95, InitialLearning = learning,
                            Inventory = scenario is "training-solo" or "hard-duo" or "rollback" ? GuildTrainingEntryTcpTests.Coins(candidate.Inventory, resources) : scenario == "ticket-solo" ? candidate.Inventory with { Items = candidate.Inventory.Items.Add(
                                new(0, new(InventoryListType.Main, 65), new(10310974, ItemKind.Consumable, 1) { ExpireTime = 2145852000 })) } : candidate.Inventory });
                        slot = role.Slot;
                        if (scenario is "training-solo" or "hard-duo" or "rollback") GuildTrainingEntryTcpTests.SelectTraining(fixture, role.Id);
                        if (i == count - 1 && scenario == "fatigue")
                            fixture.Sql("UPDATE character_fatigue SET remaining=29,used=maximum-29 WHERE character_id=$p0", role.Id);
                        if (i == count - 1 && scenario is "daily" or "weekly" or "ticket-solo")
                        {
                            var period = DungeonIncountPolicy.Period(fixture.UtcNow);
                            fixture.Sql("INSERT INTO character_dungeon_incount VALUES($p0,$p1,5428,$p2,$p3,$p4,$p5)",
                                role.Id, account, period.Day, scenario is "daily" or "ticket-solo" ? 1 : 0, period.Week, scenario is "weekly" or "ticket-solo" ? 2 : 0);
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
                    byte partyKind = scenario == "wrong-kind" ? (byte)0 : (byte)5;
                    ushort partyMode = scenario == "wrong-mode" ? (ushort)3 : mode;
                    var created = All()[0].Last(packet => packet is { Kind: 0, Opcode: 9 } && U16(packet.Body, 2) == 9999).Body;
                    That(created.Length == 63 && created[30] == partyKind && U16(created, 31) == partyMode &&
                        created[^2] == partyKind && created[^1] == 0,
                        "CMD12名册的设置与最终类型均为原请求值，不能在芮兹菜单前清成普通队伍");
                    foreach (var peer in peers.Skip(1))
                    {
                        leader.Send(10, Build(writer => { writer.Write(peer.Id); writer.Write((byte)0); writer.Write(0u); writer.Write(new byte[6]); }));
                        All();
                        peer.Send(11, Build(writer => { writer.Write(leader.Id); writer.Write((byte)0); writer.Write(0u); }));
                        That(All(peer).Take(peers.IndexOf(peer) + 1).All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 9 } &&
                            U16(packet.Body, 2) == 9999 && packet.Body[^2] == partyKind)),
                            "所有队员加入时最终安装同一Party类型，不能把魔界队伍覆盖为0");
                    }
                    if (scenario == "hard")
                    {
                        leader.Send(121, [1]);
                        leader.Expect(1, 121, [1]);
                        That(All().All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 9 } &&
                            packet.Body[9] == 2 && packet.Body[^2] == 5 && packet.Body[^8] == 1)),
                            "委任后的成员增量保持魔界类型5及新队长，开始菜单资格随真实队长转移");
                        peers[1].Send(121, [0]);
                        peers[1].Expect(1, 121, [1]);
                        That(All(peers[1]).All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 9 } &&
                            packet.Body[9] == 2 && packet.Body[^2] == 5 && packet.Body[^8] == 0)),
                            "重新委任原队长不清除魔界类型，继续使用同一模式开战");
                    }
                }
                if (scenario == "ticket-solo")
                {
                    leader.Send(1652);
                    That(All()[0].Any(packet => packet is { Kind: 1, Opcode: 1652, Body: [0, 0, 0] }), "日周耗尽时魔界开战拒绝");
                    leader.Send(515, Build(w => { w.Write((ushort)65); w.Write((byte)0); w.Write(0u); w.Write(214u); w.Write(0u); w.Write(0u); }));
                    leader.Expect(1, 515, Convert.FromHexString("01410000D6000000"));
                    leader.Expect(0, 550, Convert.FromHexString("341500000101")); All();
                }
                string assets = fixture.Snapshot();
                leader.Send(15, BitConverter.GetBytes(5426u));
                That(All()[0].Any(packet => packet is { Kind: 1, Opcode: 15 } && packet.Body[0] == 0) && fixture.Snapshot() == assets,
                    "开战前的15不能绕过专用准入");
                var requester = scenario == "not-leader" ? peers[1] : leader;
                requester.Send(1652);
                var started = All(requester);
                bool accepted = scenario is "rollback" or "hard" or "normal" or "guide" or "training-solo" or
                    "normal-solo" or "hard-solo" or "hard-duo" or "normal-trio" or "ticket-solo";
                That(started[peers.IndexOf(requester)].Any(packet => packet is { Kind: 1, Opcode: 1652 } && packet.Body.SequenceEqual(accepted ? new byte[] { 1 } : new byte[] { 0, 0, 0 })) &&
                    fixture.Snapshot() == assets, "1652资格拒绝或冻结战役，首次进图前不提前扣费: " + scenario);
                if (!accepted)
                {
                    That(started.All(rows => !rows.Any(packet => packet.Opcode is 1671 or 28 or 29)), "拒绝不发布战斗状态或进图成功");
                    continue;
                }
                int dungeon = mode == 0 ? 5420 : mode == 1 ? 5426 : 5440;
                That(started.All(rows => rows.Single(packet => packet is { Kind: 0, Opcode: 1671 }).Body is { Length: 45 } body &&
                    body[0] == 2 && body[1] == mode && U32(body, 2) == catalog.LimitTime &&
                    U32(body, 6) == 5 && U32(body, 10) == dungeon), "1671先安装本模式五个真实PVF副本，客户端右侧面板可选");
                var info = started[0].Single(packet => packet is { Kind: 0, Opcode: 1671 }).Body;
                int[] stageOrder = scenario == "hard-solo"
                    ? [..Enumerable.Range(0, 4).Where(index => info[15 + index * 7] < 2), 4]
                    : [0, 1, 2, 3, 4];
                That(Enumerable.Range(0, 4).Select(i => info[15 + i * 7]).Order().SequenceEqual(new byte[] { 0, 1, 2, 3 }) && info[44] == 3 &&
                    started.All(rows => rows.Single(packet => packet is { Kind: 0, Opcode: 1671 }).Body.AsSpan(6).SequenceEqual(info.AsSpan(6))),
                    "四图属性只抽一次且全队一致，王座开局封锁");
                leader.Send(15, BitConverter.GetBytes((uint)(mode == 0 ? 5416 : mode == 1 ? 5422 : 5436)));
                That(All()[0].Any(packet => packet is { Kind: 1, Opcode: 15 } && packet.Body[0] == 0), "不能直接绕过两层结界进入王座");
                leader.Send(1652);
                That(All()[0].Any(packet => packet is { Kind: 1, Opcode: 1652, Body: [0, 0, 0] }), "重复开战不刷新截止时间或产生第二次计费");
                dungeon = checked((int)U32(info, 10 + stageOrder[0] * 7));
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
                    foreach (var peer in peers) GuildTrainingEntryTcpTests.CheckCoins(fixture, peer.Id, 20);
                    fixture.Sql("DROP TRIGGER reject_war");
                    continue;
                }
                var entered = All();
                That(entered.All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 1671 } &&
                    packet.Body[16 + stageOrder[0] * 7] == 1)), "全员入场事务完成后同步1671，将选中前置图置为战斗中");
                That(entered.All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 28 }) && rows.Any(packet => packet is { Kind: 0, Opcode: 29 })) &&
                    entered.All(rows => Array.FindIndex(rows, packet => packet.Opcode == 28) < Array.FindIndex(rows, packet => packet.Opcode == 29)),
                    "普通/困难/引导均沿正式28→29创建真实地图和怪物: " + scenario);
                foreach (var peer in peers)
                    That(fixture.Query("SELECT used FROM character_fatigue WHERE character_id=$p0", peer.Id) is [[30L]] &&
                        fixture.Query("SELECT used FROM character_weekly_dungeon WHERE character_id=$p0 AND slot=7", peer.Id) is [[1L]] &&
                        fixture.Query("SELECT daily_used,weekly_used FROM character_dungeon_incount WHERE character_id=$p0 AND dungeon=5428", peer.Id)
                            .Single().SequenceEqual(scenario == "ticket-solo" ? new object?[] { 2L, 3L } : new object?[] { 1L, 1L }),
                        "每人一次扣30疲劳并记5428日周次数、冒险团周统计");
                leader.Send(16, selection);
                All();
                foreach (var peer in peers) { peer.Send(37, new byte[8]); All(peer); }
                if (scenario is "training-solo" or "hard-duo")
                    foreach (var peer in peers) GuildTrainingEntryTcpTests.CheckCoins(fixture, peer.Id, 19);
                That(fixture.Snapshot() != assets && peers.All(peer => fixture.Query("SELECT used FROM character_fatigue WHERE character_id=$p0", peer.Id) is [[30L]]),
                    "重复16和加载不再次收费，不按房间扣魔界大战疲劳");
                if (scenario is "hard" or "guide" or "normal-solo" or "hard-solo")
                {
                    for (int step = 0; step < stageOrder.Length; step++)
                    {
                        int stage = stageOrder[step];
                        uint stageDungeon = U32(info, 10 + stage * 7);
                        var stageEntry = entered[0].Single(packet => packet is { Kind: 0, Opcode: 28 }).Body;
                        byte[] map = entered[0].Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
                        uint actualDungeon = U32(stageEntry, 0);
                        var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(checked((int)actualDungeon)).Mazes[stageEntry[7]];
                        That(actualDungeon == stageDungeon || stage < 4 && actualDungeon == catalog.HiddenDungeons.Single(row => row.Mode == mode).Id,
                            "28按冻结路线进入本模式真实副本，面板仍保留五图");
                        var visited = new HashSet<DungeonPoint>();
                        byte[]? completed = null;
                        byte[]? lastDeath = null;
                        int rewardNotices = 0;
                        for (int room = 0; room < 64 && completed is null; room++)
                        {
                            if (room > 0 || step > 0)
                                foreach (var peer in peers) { peer.Send(37, new byte[8]); All(peer); }
                            var point = new DungeonPoint(map[0], map[1]);
                            visited.Add(point);
                            foreach (int offset in Enumerable.Range(0, map[18]).Select(i => 19 + i * 22)
                                .Where(i => map[i + 13] != 9 && map[i + 14] == 0).OrderBy(i => map[i + 13] == 3))
                            {
                                lastDeath = FormalDungeonTcpTests.Death(U16(map, offset + 6), leader.Id, map[offset + 12]);
                                leader.Send(39, lastDeath);
                                var killed = All();
                                if (killed.Any(rows => rows.Any(packet => packet.Opcode == 1673)))
                                {
                                    rewardNotices++;
                                    That(stage == 4 && killed.All(rows => rows.Count(packet => packet is { Kind: 0, Opcode: 1673 }) == 1),
                                        "仅王座真实完成后向每位成员发布一次1673");
                                    foreach (var peer in peers)
                                    {
                                        That(fixture.Query("SELECT count(*) FROM character_evildom_war_rewards WHERE character_id=$p0", peer.Id) is [[1L]] &&
                                            fixture.Query("SELECT count(*) FROM mailbox_messages WHERE recipient_id=$p0", peer.Id) is [[> 0L]],
                                            "显示奖励之前每人独立回执与永久奖励邮件已提交");
                                        string json = (string)fixture.Query("SELECT reward_json FROM character_evildom_war_rewards WHERE character_id=$p0 AND delivered=1", peer.Id)[0][0];
                                        var saved = JsonSerializer.Deserialize<EvildomWarRewardSnapshot>(json)!;
                                        That(saved.Rule == EvildomWarRewardPolicy.Rule && saved.Reward.Items.All(row => row.Group != 1),
                                            "新王座快照不重复发放实验室固定组");
                                        var groups = saved.Reward.Items.GroupBy(row => row.Group).ToArray();
                                        byte[] expected = Build(writer =>
                                        {
                                            writer.Write(0u); writer.Write((byte)groups.Length);
                                            foreach (var group in groups)
                                            {
                                                writer.Write(group.Key); writer.Write((byte)group.Count());
                                                foreach (var row in group) { writer.Write((byte)0); writer.Write(row.ItemId); writer.Write((uint)row.Count); }
                                            }
                                        });
                                        That(killed[peers.IndexOf(peer)].Single(packet => packet.Opcode == 1673).Body.SequenceEqual(expected),
                                            "1673独立读宽及每行物品数量逐字节等于已提交奖励");
                                        var cores = fixture.Query("""
                                            SELECT i.item_core FROM mailbox_messages m JOIN mailbox_attachments a ON a.mail_id=m.mail_id
                                            JOIN character_inventory_items i ON i.item_uid=a.item_uid
                                            WHERE m.recipient_id=$p0 AND m.delivery_key LIKE 'evildom-war:%' ORDER BY m.mail_id,a.ordinal
                                            """, peer.Id).Select(row => JsonSerializer.Deserialize<ItemCore>((string)row[0])!).ToArray();
                                        That(cores.SequenceEqual(saved.Items.Select(item => item.Core)),
                                            "邮件持久化全部完整核心含护石孔色，与冻结奖励逐件一致");
                                    }
                                }
                                That(killed.All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 38 })), "魔界正式怪物死亡逐成员提交后才广播");
                                var changed = killed.Select(rows => rows.LastOrDefault(packet => packet is { Kind: 0, Opcode: 1671 })?.Body).ToArray();
                                if (changed[0] is not null)
                                {
                                    completed = changed[0]!;
                                    bool sealsCleared = stageOrder.Take(step + 1).Count(index => index < 4 && info[15 + index * 7] < 2) == 2;
                                    That(changed.All(body => body is not null && body.AsSpan().SequenceEqual(completed)) &&
                                        completed[16 + stage * 7] == 2 && completed[44] == (stage == 4 ? 2 : sealsCleared ? 0 : 3),
                                        "全队真实通关同步完成状态，钥匙魔法阵各解一层，剑盾可选");
                                }
                            }
                            if (actualDungeon != stageDungeon && point == new DungeonPoint(stageEntry[8], stageEntry[9]))
                            {
                                That(completed is null && maze.ClearConditions is [{ Type: "destroy object", Count: 1 }],
                                    "隐藏图杀怪不冒充物件终末");
                                byte[] passive = new byte[52];
                                BinaryPrimitives.WriteUInt32LittleEndian(passive, checked((uint)maze.ClearConditions.Single().TargetId));
                                BinaryPrimitives.WriteUInt16LittleEndian(passive.AsSpan(4), ushort.MaxValue);
                                passive[37] = 1; BinaryPrimitives.WriteUInt32LittleEndian(passive.AsSpan(42), 1);
                                FormalDungeonTcpTests.SealDeath(passive);
                                leader.Send(39, passive);
                                var cleared = All();
                                completed = cleared[0].Single(packet => packet is { Kind: 0, Opcode: 1671 }).Body;
                                That(cleared.All(rows => rows.Count(packet => packet is { Kind: 0, Opcode: 1671 }) == 1 &&
                                        !rows.Any(packet => packet.Opcode == 1673)) && completed[16 + stage * 7] == 2,
                                    "真实物件持久通关推进被替代前置图，不提前发整场奖励");
                                foreach (var peer in peers)
                                {
                                    string json = (string)fixture.Query("SELECT reward_json FROM character_evildom_war_hidden_rewards WHERE character_id=$p0", peer.Id)[0][0];
                                    var saved = JsonSerializer.Deserialize<EvildomWarRewardSnapshot>(json)!;
                                    saved.Validate();
                                    That(saved.Rule == EvildomWarRewardPolicy.HiddenRule && saved.Reward.Items is [{ Group: 1, ItemId: 10310786 }] &&
                                        saved.Reward.Items[0].Count == (mode == 2 ? 9 : 15) &&
                                        cleared[peers.IndexOf(peer)].Any(packet => packet is { Kind: 0, Opcode: 99 }),
                                        "真实物件终点经正式Handler提交本人固定额外奖后通知邮箱");
                                    var cores = fixture.Query("""
                                        SELECT i.item_core FROM mailbox_messages m JOIN mailbox_attachments a ON a.mail_id=m.mail_id
                                        JOIN character_inventory_items i ON i.item_uid=a.item_uid
                                        WHERE m.recipient_id=$p0 AND m.delivery_key LIKE 'evildom-war-hidden:%' ORDER BY m.mail_id,a.ordinal
                                        """, peer.Id).Select(row => JsonSerializer.Deserialize<ItemCore>((string)row[0])!).ToArray();
                                    That(cores.SequenceEqual(saved.Items.Select(item => item.Core)),
                                        "实验室独立永久邮件附件逐件等于隐藏快照，和王座邮件分开核对");
                                }
                                string hiddenAssets = fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id");
                                leader.Send(39, passive);
                                That(All().All(rows => rows.Count(packet => packet.Opcode == 1671) == 1 && !rows.Any(packet => packet.Opcode == 1673)) &&
                                    fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id") == hiddenAssets,
                                    "重复隐藏物件仅重同步原战况，不重复邮件或王座奖");
                            }
                            if (completed is not null) break;
                            var next = maze.StaticTopology.Links!.SelectMany(link => new[] { (link.First, link.Second), (link.Second, link.First) })
                                .Where(edge => edge.Item1 == point).Select(edge => edge.Item2).OrderBy(p => visited.Contains(p)).First();
                            byte[] move = new byte[100]; move[0] = checked((byte)next.X); move[1] = checked((byte)next.Y);
                            leader.Send(45, move);
                            var moved = All();
                            That(moved.All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 29 })), "魔界清房后沿真实迷宫连接全队过房");
                            map = moved[0].Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
                        }
                        That(completed is not null, "真实魔界Run必须在有界路线内产生通关事实");
                        if (scenario == "hard-solo" && stage == 4)
                        {
                            var checkpoint = JsonSerializer.Deserialize<EvildomWarCheckpoint>((string)fixture.Query(
                                "SELECT checkpoint_json FROM evildom_war_battles WHERE battle_id=(SELECT battle_id FROM character_evildom_war_seats WHERE character_id=$p0)", leader.Id)[0][0])!;
                            checkpoint.Validate();
                            That(checkpoint.View.Completed && checkpoint.View.Dungeons.Count(row => row.Kind == 0 && row.State == 0) == 2 &&
                                Enumerable.Range(0, 4).Where(index => info[15 + index * 7] >= 2).All(index => completed![16 + index * 7] == 0),
                                "困难单人真实双结界后直达王座，跳过剑盾仍持久完成并获得奖励");
                        }
                        That(rewardNotices == (stage == 4 ? 1 : 0), "王座必发且仅发一次1673，前置图不发");
                        foreach (var peer in peers)
                            That(fixture.Query("SELECT count(*) FROM character_evildom_war_rewards WHERE character_id=$p0", peer.Id)
                                is [[long rewards]] && rewards == (stage == 4 ? 1 : 0), "前置图不发整场奖励，王座完成必须发奖");
                        if (lastDeath is not null)
                        {
                            string clearedAssets = fixture.Snapshot();
                            string clearedRewards = fixture.Snapshot("SELECT reward_json,mail_ids,delivered FROM character_evildom_war_rewards ORDER BY character_id");
                            string clearedMails = fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id");
                            leader.Send(39, lastDeath);
                            That(All().All(rows => !rows.Any(packet => packet.Opcode == 1673) &&
                                (stage == 4 || actualDungeon != stageDungeon || !rows.Any(packet => packet.Opcode == 1671))) && fixture.Snapshot() == clearedAssets &&
                                fixture.Snapshot("SELECT reward_json,mail_ids,delivered FROM character_evildom_war_rewards ORDER BY character_id") == clearedRewards &&
                                fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id") == clearedMails,
                                "重复末怪可以同步最终战况但不重复弹奖或写入资产");
                        }
                        foreach (var peer in peers)
                        {
                            peer.Send(46, DungeonSettlementProtocolTests.Report(peers.Select(member => (member.Id, (byte)99)).ToArray()));
                            That(All(peer)[peers.IndexOf(peer)].Any(packet => packet is { Kind: 0, Opcode: 34 }), "战役前置图复用真实角色结算后允许返回");
                            // 原著小图没有普通白图翻牌；发35会让客户端开翻牌层，撤退与再选图都被结算阶段挡住。
                            That(All(peer)[peers.IndexOf(peer)].All(packet => packet.Opcode != 35), "魔界小图不发普通白图通关翻牌35");
                        }
                        leader.Send(72, [1, 2]);
                        That(All().All(rows => rows.Any(packet => packet is { Kind: 1, Opcode: 72 })), "魔界全队结算返回等待区保持原战役");
                        if (scenario is "normal-solo" or "hard" && stage == 0)
                        {
                            int reconnectIndex = scenario == "normal-solo" ? 0 : 2;
                            var departing = peers[reconnectIndex];
                            ushort originalId = departing.Id;
                            string account = (string)fixture.Query("SELECT account FROM characters WHERE id=$p0", originalId)[0][0];
                            var checkpoint = fixture.Snapshot("SELECT battle_id,revision,deadline,checkpoint_json FROM evildom_war_battles WHERE battle_id=(SELECT battle_id FROM character_evildom_war_seats WHERE character_id=$p0)", originalId);
                            int disconnected = fixture.Events().Count(row => row.GetProperty("Kind").GetString() == "disconnected");
                            departing.Dispose();
                            for (int wait = 0; wait < 250 && fixture.Events().Count(row => row.GetProperty("Kind").GetString() == "disconnected") <= disconnected; wait++)
                                await Task.Delay(20);
                            That(fixture.Query("SELECT state FROM character_evildom_war_seats WHERE character_id=$p0", originalId) is [[1L]],
                                "真实TCP断开将已付费席位置为300秒可认领状态");
                            var reconnect = Open(fixture, account, gamePort: endpoints[41], channelNumber: 41);
                            peers[reconnectIndex] = reconnect;
                            if (reconnectIndex == 0) leader = reconnect;
                            reconnect.Select(0, prepared: true, resumeTown: true, room: false);
                            var recovered = All(reconnect)[reconnectIndex];
                            That(reconnect.Id == originalId && recovered.Any(packet => packet is { Kind: 0, Opcode: 9 }) &&
                                recovered.Single(packet => packet is { Kind: 0, Opcode: 1671 }).Body[16] == 2 &&
                                fixture.Snapshot("SELECT battle_id,revision,deadline,checkpoint_json FROM evildom_war_battles WHERE battle_id=(SELECT battle_id FROM character_evildom_war_seats WHERE character_id=$p0)", originalId) == checkpoint &&
                                fixture.Query("SELECT state FROM character_evildom_war_seats WHERE character_id=$p0", originalId) is [[0L]],
                                "真实重新登录恢复队伍与已清图1671，原战役、版本、截止和费用均保留");
                        }
                        leader.Send(15, BitConverter.GetBytes(stageDungeon));
                        That(All()[0].Any(packet => packet is { Kind: 1, Opcode: 15 } && packet.Body[0] == 0), "已完成的图不能重新进入或重复扣费");
                        if (step + 1 == stageOrder.Length) continue;
                        uint nextDungeon = U32(info, 10 + stageOrder[step + 1] * 7);
                        leader.Send(15, BitConverter.GetBytes(nextDungeon));
                        That(All().All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 27 })), "战役下一开放图重新建立全队选图场景");
                        BinaryPrimitives.WriteUInt32LittleEndian(selection, nextDungeon);
                        leader.Send(16, selection);
                        entered = All();
                        That(entered.All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 28 })), "后续结界及王座仍走正式全员入场");
                    }
                    foreach (var peer in peers)
                        That(fixture.Query("SELECT used FROM character_fatigue WHERE character_id=$p0", peer.Id) is [[30L]] &&
                            fixture.Query("SELECT used FROM character_weekly_dungeon WHERE character_id=$p0 AND slot=7", peer.Id) is [[1L]] &&
                            fixture.Query("SELECT daily_used,weekly_used FROM character_dungeon_incount WHERE character_id=$p0 AND dungeon=5428", peer.Id) is [[1L, 1L]],
                            "同一战役五图仅计一次疲劳、日周次数及冒险团周统计");
                }
            }
            finally { foreach (var peer in peers) peer.Dispose(); }
        }
        GuideBindings(fixture, endpoints[41], resources, catalog);
        GuideDirectory(fixture, endpoints[41], resources, catalog);
        RoomExit(fixture, endpoints[41], resources);
        Console.WriteLine($"PASS evildom war TCP {Count - before}");
    }

    static void GuideDirectory(TcpTestFixture fixture, int port, TcpTestResources resources, EvildomWarCatalog catalog)
    {
        using var leader = Participant("wardirectorylead", "WarDirLead");
        using var observer = Participant("wardirectorywatch", "WarDirWatch");
        Drain(leader); Drain(observer);
        leader.Send(12, Information(0));
        var roster = Drain(leader).Single(packet => packet is { Kind: 0, Opcode: 9 }).Body;
        ushort party = U16(roster, 4);
        Observe(0);
        leader.Send(12, Information(2));
        That(Drain(leader).Any(packet => packet is { Kind: 0, Opcode: 9 }), "切换引导仍发送本人完整名册");
        // 同屏公告也必须移除原普通队伍；仅过滤显式查询会留下旧行。
        Observe(3);
        Query(0, 2, send: true);
        foreach (ushort mode in new ushort[] { 0, 1 })
        {
            leader.Send(12, Information(mode)); Drain(leader);
            Query(1, mode);
        }

        TcpPeer Participant(string account, string name)
        {
            ushort slot;
            using (var owner = new AccountOwners().Acquire(account, () => { }))
            {
                var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(0), catalog.WaitingArea,
                    catalog.WaitingArea, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
                slot = new CharacterRepository(fixture.Database).Create(owner, candidate with
                    { Level = 95, AcceptedStoryLevel = 95, InitialLearning = learning }).Slot;
            }
            var peer = Open(fixture, account, gamePort: port, channelNumber: 41);
            peer.Select(slot, prepared: true, resumeTown: true, room: false);
            return peer;
        }
        void Observe(byte action)
        {
            for (int i = 0; i < 100; i++)
            {
                var packet = observer.Read();
                if (packet is { Kind: 0, Opcode: 9 } && packet.Body.Length >= 11 && U16(packet.Body, 2) == 9999 &&
                    U16(packet.Body, 4) == party && packet.Body[9] == action) return;
                That(packet.Kind == 0, "队伍同屏目录只包含通知");
            }
            throw new InvalidOperationException("FAIL 队伍目录公告未收敛");
        }
        void Query(ushort count, ushort mode, bool send = false)
        {
            // 1404订阅会随队伍变化主动刷新；模式切换只消费该刷新，不能再遗留一次同形查询。
            if (send)
            {
                byte[] request = new byte[58]; request[2] = 1; request[24] = 5;
                BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(15), observer.Id);
                "GuideDirectory"u8.CopyTo(request.AsSpan(26));
                observer.Send(1404, request);
            }
            for (int i = 0; i < 100; i++)
            {
                var packet = observer.Read();
                if (packet is { Kind: 0, Opcode: 9 } && U16(packet.Body, 2) != 9999)
                {
                    That(U16(packet.Body) == count && U16(packet.Body, 2) == count &&
                        (count == 0 ? packet.Body.Length == 4 : U16(packet.Body, 4) == party),
                        "魔界寻找队伍过滤引导，普通困难仍列出，页数随过滤结果计算");
                    // Directory头4B；固定14B标题的完整info块中KindArgument位于42（action0另有1B标记）。
                    if (count != 0) That(packet.Body.Length >= 44 && U16(packet.Body, 42) == mode,
                        "目录订阅刷新对应本次切换的普通或困难模式");
                    return;
                }
                That(packet.Kind == 0, "查询等待期间只消费目录和城镇通知");
            }
            throw new InvalidOperationException("FAIL 队伍目录查询未响应");
        }
        static byte[] Information(ushort mode) => Build(writer =>
        {
            writer.Write((byte)0); writer.Write((byte)0); writer.Write(14); writer.Write("GuideDirectory"u8);
            writer.Write((byte)4); writer.Write(0u); writer.Write((byte)5); writer.Write((ushort)0); writer.Write((byte)0);
            writer.Write((byte)5); writer.Write(mode); writer.Write((byte)7);
        });
    }

    static void GuideBindings(TcpTestFixture fixture, int port, TcpTestResources resources, EvildomWarCatalog catalog)
    {
        const string account = "warguidewire";
        var repository = new CharacterRepository(fixture.Database);
        DfoServer.Game.Characters.Character[] roles;
        using (var owner = new AccountOwners().Acquire(account, () => { }))
            roles = Enumerable.Range(0, 3).Select(index =>
            {
                var candidate = CharacterCreation.Prepare("GuideWire" + index, resources.Characters.Get(0),
                    catalog.WaitingArea, catalog.WaitingArea, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
                return repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95, InitialLearning = learning });
            }).ToArray();
        foreach (int index in new[] { 0, 1, 2, 0 })
        {
            using var peer = Open(fixture, account, gamePort: port, channelNumber: 41);
            peer.Select(roles[index].Slot, prepared: true, resumeTown: true, room: false);
            Drain(peer);
            peer.Send(12, Information(2));
            var packets = Drain(peer);
            if (index == 2)
            {
                That(packets.Any(packet => packet is { Kind: 1, Opcode: 12 } && packet.Body.SequenceEqual(new byte[] { 0, 21, 0 })) &&
                    !packets.Any(packet => packet is { Kind: 0, Opcode: 9 }) &&
                    fixture.Query("SELECT count(*) FROM account_evildom_war_guides WHERE account=$p0", account) is [[2L]],
                    "真实CMD12第三角色引导失败不造名册或多占名额");
                peer.Send(12, Information(0));
                That(Drain(peer).Any(packet => packet is { Kind: 0, Opcode: 9 }), "引导名额耗尽后仍可创建普通队伍");
                peer.Send(1652, []);
                That(Drain(peer).Any(packet => packet is { Kind: 1, Opcode: 1652 } && packet.Body[0] == 1),
                    "真实普通开战不受引导账号名额限制");
            }
            else
                That(packets.Any(packet => packet is { Kind: 0, Opcode: 9 }) &&
                    fixture.Query("SELECT count(*) FROM account_evildom_war_guides WHERE account=$p0 AND character_id=$p1", account, peer.Id) is [[1L]],
                    "真实CMD12即持久绑定前两个角色，重登原绑定幂等");
        }

        static byte[] Information(ushort mode) => Build(writer =>
        {
            writer.Write((byte)0); writer.Write((byte)0); writer.Write(3); writer.Write("war"u8);
            writer.Write((byte)4); writer.Write(0u); writer.Write((byte)5); writer.Write((ushort)0); writer.Write((byte)0);
            writer.Write((byte)5); writer.Write(mode); writer.Write((byte)7);
        });
    }

    // 线上 2026-10-27 23:34–23:38（Connection 83）三次 CMD1426 全部落房间式 67/0 445,377，客户端随后自己发 CMD36 才到 67/1，
    // 且房间式落点额外发一条取角色 Return（38/54）的 1379。魔界大战频道出门一次就应落到 67/1 广场（Issue #833）。
    static void RoomExit(TcpTestFixture fixture, int port, TcpTestResources resources)
    {
        const string account = "warroomexit";
        ushort slot;
        using (var owner = new AccountOwners().Acquire(account, () => { }))
        {
            var candidate = CharacterCreation.Prepare("WarRoomExit", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
            slot = new CharacterRepository(fixture.Database).Create(owner, candidate with
                { Level = 95, AcceptedStoryLevel = 95, InitialLearning = learning }).Slot;
        }
        using var peer = Open(fixture, account, gamePort: port, channelNumber: 41);
        // 38/1 不在魔界大战落点城镇 67，ClientKeepsTownSnapshot 为假，服务端按新入城重落共享房间而不是恢复快照，
        // 客户端包序等同普通选角（22/23/24/1379/124）；resumeTown:true 会让夹具按恢复顺序期待23后紧跟1379。
        peer.Select(slot, prepared: true, resumeTown: false, room: true);
        That(fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[38L, 1L]],
            "夹具角色真实处在共享房间38/1");
        peer.Send(1426);
        var notices = Drain(peer);
        var area = notices.Single(packet => packet is { Kind: 0, Opcode: 23 }).Body;
        That(area.Length == 10 && U16(area) == peer.Id && area[2] == 67 && area[3] == 1 &&
            U16(area, 4) == 656 && U16(area, 6) == 154 && area[8] == 0 && area[9] is 1 or 3 &&
            notices.Any(packet => packet is { Kind: 0, Opcode: 24 }),
            $"魔界大战房间出门一次直接落67/1广场656,154，不需要第二次门: {area[2]}/{area[3]} {U16(area,4)},{U16(area,6)}");
        That(notices.All(packet => !(packet is { Kind: 0, Opcode: 1379 })),
            "广场不是房间式区域，出门后不再发返回城镇取角色Return的1379");
        That(fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", peer.Id) is [[67L, 1L, 656L, 154L]],
            "落点已作为角色位置提交");
    }
}
