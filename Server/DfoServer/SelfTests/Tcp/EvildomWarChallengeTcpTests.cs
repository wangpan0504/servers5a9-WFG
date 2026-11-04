using System.Buffers.Binary;
using DfoServer.Development.Dungeons;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Management;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.PartyDungeonTcpTests;

namespace DfoServer.Testing;

internal static class EvildomWarChallengeTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        EvildomWarChallengeTests.RunCore();
        EvildomWarChallengeTests.RunResources(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
        const int recoveryPotionId = 1026;
        That(resources.ItemResources.RecoveryConsumables.TryGet(recoveryPotionId, out var recoveryPotion) &&
            recoveryPotion.Allows(95, 0) && recoveryPotion.AllowedDungeons is null &&
            recoveryPotion.EffectMilliseconds == 600000 && (recoveryPotion.EffectMaintenance & 2) == 0,
            "英雄恢复夹具取候选真实食人魔药剂，95级可用且持续药效须在Town边界清理");
        var catalog = SelfTestCatalogs.Dungeons(resources.Archive);
        var definition = catalog.Get(400000105);
        var maze = definition.Mazes.Single();
        That(definition.IsEvildomWarChallenge && maze.MapSpecification is { Count: 15 } &&
            maze.WarpMaps.Any(warp => warp.Source == new DungeonPoint(12, 0) && warp.Destination == new DungeonPoint(12, 1)) &&
            maze.WarpMaps.Any(warp => warp.Source == new DungeonPoint(12, 1) && warp.Destination == new DungeonPoint(12, 0)),
            "英雄TCP绑定候选15张真实地图及双向附属房传送声明");
        var waiting = ChannelAreaLanding.DecideArea(resources.Towns, 124, 2, 95, new HashSet<int>()).Spot
            ?? throw new InvalidDataException("Challenge waiting area has no standable entrance");
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "evildom-war-challenge"), resources: resources, gm: true);
        await fixture.StartAsync();
        string Assets() => string.Join('\n', new[]
        {
            fixture.Snapshot(),
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid"),
            fixture.Snapshot("SELECT * FROM character_consumable_states ORDER BY character_id,kind,item_id"),
            fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id"),
            fixture.Snapshot("SELECT * FROM mailbox_attachments ORDER BY mail_id,ordinal"),
            fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts ORDER BY character_id,instance_id"),
            fixture.Snapshot("SELECT * FROM character_dungeon_card_rewards ORDER BY character_id,instance_id"),
            fixture.Snapshot("SELECT * FROM character_dungeon_card_options ORDER BY character_id,instance_id")
        });
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out _),
            "英雄TCP使用实际疲劳，不借开发无限疲劳放行");
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        int sequence = 0;
        foreach (var (count, returnToSelection) in new[] { (1, false), (4, false), (4, true) })
        {
            var peers = new List<TcpPeer>();
            try
            {
                for (int index = 0; index < count; index++)
                {
                    string account = "hero" + sequence++;
                    ushort slot;
                    using (var owner = new AccountOwners().Acquire(account, () => { }))
                    {
                        var origin = index == 1 ? new DfoServer.GameWorld.Towns.Location(67, 1, 656, 154) :
                            index == 2 ? resources.Towns.Room : resources.Towns.InitialReturn;
                        var candidate = CharacterCreation.Prepare("Hero" + sequence, resources.Characters.Get(0),
                            origin, resources.Towns.InitialReturn, resources.Learning.Get);
                        var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                        { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
                        var repository = new CharacterRepository(fixture.Database);
                        var role = repository.Create(owner, candidate with
                        { Level = 95, AcceptedStoryLevel = 95, InitialLearning = learning });
                        var items = resources.ItemResources;
                        var granted = InventoryRewardGrantService.TryGrantBatch(role, [new(recoveryPotionId, 2)],
                            items.Items, items.GoldLimits, items.EpicPieces, DateTimeOffset.UtcNow);
                        slot = InventoryService.Commit(repository, owner, role, granted).Slot;
                    }
                    var peer = Open(fixture, account, gamePort: endpoints[56], channelNumber: 56);
                    peers.Add(peer);
                    peer.Select(slot, prepared: true, room: false);
                    That(peer.LoginEndpoint[3] == 64 && fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[124L, 1L]],
                        "普通存档、旧67及共享房间自然LOGIN64直接到英雄广场124/1，不借等待区快照");
                    peer.Send(2, Build(writer =>
                    {
                        writer.Write((byte)5); writer.Write(0x0100007fu); writer.Write(0x0100007fu);
                        writer.Write((ushort)(33000 + index)); writer.Write(512u);
                    }));
                    Drain(peer);
                }
                var roomIdentities = new Dictionary<ushort, byte[]>();
                var keyOffers = new Dictionary<ushort, byte[]>();
                bool withholdLastLease = count == 4 && !returnToSelection;
                WirePacket[][] All(TcpPeer? caller = null)
                {
                    caller ??= peers[0];
                    var rows = new WirePacket[peers.Count][];
                    foreach (var peer in peers.OrderBy(peer => peer == caller ? 0 : 1))
                    {
                        var frames = withholdLastLease && ReferenceEquals(peer, peers[3]) ? Drain(peer) :
                            AntonTcpProbe.Barrier(peer, "hero room and key barrier").ToArray();
                        rows[peers.IndexOf(peer)] = frames;
                        foreach (var packet in frames)
                        {
                            if (packet is { Kind: 0, Opcode: MonsterAiPackets.Opcode }) roomIdentities[peer.Id] = packet.Body;
                            if (packet is { Kind: 0, Opcode: FiendwarKeyLeaseRequests.Opcode, Body.Length: 80 } && packet.Body[4] == 1)
                                keyOffers[peer.Id] = packet.Body;
                        }
                    }
                    return rows;
                }
                var leader = peers[0];
                All();
                leader.Send(12, Convert.FromHexString("000304000000000500000000000007"));
                That(All()[0].Any(packet => packet is { Kind: 0, Opcode: 9 }) &&
                    fixture.Query("SELECT town,area FROM characters WHERE id=$p0", leader.Id) is [[124L, 1L]],
                    "英雄广场通过真实CMD12创建普通队伍，不依赖54的NPC创建或开始战役");
                foreach (var peer in peers.Skip(1))
                {
                    leader.Send(10, Build(writer => { writer.Write(peer.Id); writer.Write((byte)0); writer.Write(0u); writer.Write(new byte[6]); }));
                    All();
                    peer.Send(11, Build(writer => { writer.Write(leader.Id); writer.Write((byte)0); writer.Write(0u); }));
                    That(All(peer).Take(peers.IndexOf(peer) + 1).All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 9 })),
                        "英雄入口复用正式邀请和加入名册，不伪造四人成员");
                }
                foreach (var peer in peers)
                {
                    peer.Send(36, Area(124, 2, waiting.X, waiting.Y, 124, 1));
                    var moved = All(peer)[peers.IndexOf(peer)];
                    if (count == 1)
                        That(moved.Any(packet => packet is { Kind: 0, Opcode: 24, Body: [124, 2, ..] }) &&
                            fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[124L, 2L]],
                            "单人英雄队伍可沿真实门进入124/2，不要求先凑满四人");
                    else
                        That(moved.Any(packet => packet is { Kind: 0, Opcode: 24, Body: [124, 2, ..] }) &&
                            fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[124L, 2L]],
                            "四真实队员从英雄广场经资源有向门进入124/2");
                }
                byte[] selection = Convert.FromHexString("030000000200000000FFFF0000000000000000000000000000");
                BinaryPrimitives.WriteUInt32LittleEndian(selection, 400000105);
                string unopened = fixture.Snapshot();
                leader.Send(16, selection);
                That(All().All(rows => !rows.Any(packet => packet is { Kind: 0, Opcode: 28 or 29 })) && fixture.Snapshot() == unopened,
                    "裸16没有本次选图票据时不入场、不提前扣费");
                leader.Send(15, BitConverter.GetBytes(400000105u));
                var selecting = All();
                That(selecting.All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 27 })),
                    count == 1 ? "单人英雄队伍从124/2按244世界图打开魔界大战英雄选图" : "四真实成员从124/2按244世界图共同选中400000105");
                leader.Send(16, selection);
                var entered = All();
                That(entered.All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 28 }) &&
                    rows.Any(packet => packet is { Kind: 0, Opcode: 29 })), "英雄真实队伍通过同一正式入场事务发布28和29");
                That(roomIdentities.Count == count && keyOffers.Count == count &&
                    peers.Take(withholdLastLease ? 3 : 4).All(peer => AntonTcpProbe.FiendwarLease(peer) != 0),
                    count == 1 ? "单人英雄首房沿正式MAI/FWL1预租，不借漩涡攻坚身份" : "英雄首房普通Enter沿正式MAI/FWL1预租，待测成员保留真实未ready状态，不借漩涡攻坚身份");
                foreach (var peer in peers)
                    That(fixture.Query("SELECT used FROM character_fatigue WHERE character_id=$p0", peer.Id) is [[0L]] &&
                        fixture.Query("SELECT count(*) FROM character_dungeon_incount WHERE character_id=$p0 AND dungeon=5428", peer.Id) is [[0L]],
                        "英雄不扣30疲劳、不借5428日周次数");
                string installed = fixture.Snapshot();
                leader.Send(16, selection);
                All();
                That(fixture.Snapshot() == installed, "重复入场16不建立第二个尝试或扣费");
                byte[] map = entered[0].Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
                byte[]? bossDeath = null;
                uint spawnSequence = 100;
                byte[] SpawnBody(TcpPeer peer, ushort parent, uint child, ushort key, uint sequence) => Build(writer =>
                {
                    byte[] room = roomIdentities[peer.Id];
                    writer.Write(FiendwarSpawnRequests.MagicV2); writer.Write(U32(room, 4)); writer.Write(U32(room, 8));
                    writer.Write(U32(room, 12)); writer.Write(U32(room, 16)); writer.Write((uint)(room[20] | room[21] << 8));
                    writer.Write(sequence); writer.Write((uint)parent); writer.Write(child); writer.Write((uint)key);
                    writer.Write(AntonTcpProbe.FiendwarLease(peer));
                });
                void Spawn(TcpPeer peer, byte[] body)
                {
                    peer.Send(FiendwarSpawnRequests.Opcode, body);
                    That(All(peer).All(rows => !rows.Any(packet => packet.Opcode == FiendwarSpawnRequests.Opcode ||
                        packet is { Kind: 0, Opcode: 38 })), "英雄动态补报仅登记原生请求，不伪造生成ACK或死亡");
                }
                void ChildDeath(ushort key, byte level, bool expected = true, bool scripted = false)
                {
                    string beforeChild = Assets();
                    byte[] death;
                    if (scripted)
                    {
                        death = new byte[74]; BinaryPrimitives.WriteUInt32LittleEndian(death, key);
                        BinaryPrimitives.WriteUInt16LittleEndian(death.AsSpan(4), leader.Id); death[41] = 1; death[42] = level;
                        BinaryPrimitives.WriteUInt32LittleEndian(death.AsSpan(70), ClientBodyChecksum.Compute(death.AsSpan(0, 70)));
                    }
                    else death = FormalDungeonTcpTests.Death(key, leader.Id, level);
                    leader.Send(39, death);
                    foreach (var rows in All())
                    {
                        var deaths = rows.Where(packet => packet is { Kind: 0, Opcode: 38 }).ToArray();
                        That(expected ? deaths is [var notice] && notice.Body.Length == 9 && U16(notice.Body) == key && U16(notice.Body, 2) == 0
                            : deaths.Length == 0, "英雄动态39仅为已登记对象向实际成员同步零掉落38；未知或冲突对象不发死亡");
                        That(!rows.Any(packet => packet is { Kind: 0, Opcode: 31 or 34 or 35 or 37 or 597 or 1530 or 1673 }),
                            "动态退休不触发清图、结算、阶段符号或普通魔界战役奖励");
                    }
                    That(Assets() == beforeChild, "动态首次、重放及无效39均不改变角色revision、经验、物品、邮件或结算账本");
                }
                var visited = new HashSet<DungeonPoint>();
                for (int x = 0; x <= 12; x++)
                {
                    foreach (var peer in peers) { peer.Send(37, new byte[8]); All(peer); }
                    That(map[0] == x && map[1] == 0, "英雄沿正式主线到达房间：" + x);
                    visited.Add(new(map[0], map[1]));
                    if (x == 12)
                    {
                        int bossAt = Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
                            .Single(at => U32(map, at + 8) == 407000556);
                        ushort dummyKey = AntonTcpProbe.TakeFiendwarKey(leader);
                        byte[] dummyReport = SpawnBody(leader, U16(map, bossAt + 6), 407000559, dummyKey, spawnSequence++);
                        byte[] wrong = (byte[])dummyReport.Clone();
                        BinaryPrimitives.WriteUInt32LittleEndian(wrong.AsSpan(32), 407000557);
                        Spawn(leader, wrong); ChildDeath(dummyKey, map[bossAt + 12], expected: false);
                        if (count == 4)
                        {
                            Spawn(peers[1], SpawnBody(peers[1], U16(map, bossAt + 6), 407000559, dummyKey, spawnSequence));
                            ChildDeath(dummyKey, map[bossAt + 12], expected: false);
                        }
                        Spawn(leader, dummyReport); Spawn(leader, dummyReport);
                        // Seal_Start的MOVE MAP与Proc_0的KICK OUT最终都是100B CMD45/kind0。
                        // 活Boss与封印演员均保留，不用死亡报告伪造开门或重建Boss。
                        byte[] warp = new byte[100]; warp[0] = 12; warp[1] = 1;
                        string fighting = fixture.Snapshot();
                        byte[] unsupported = new byte[100]; unsupported[0] = 12; unsupported[1] = 2;
                        leader.Send(45, unsupported);
                        That(All().All(rows => !rows.Any(packet => packet is { Kind: 0, Opcode: 29 })),
                            "活Boss不能按Greed或未证12,2目标伪造另一传送分支");
                        warp[10] = 1;
                        leader.Send(45, warp);
                        That(All().All(rows => !rows.Any(packet => packet is { Kind: 0, Opcode: 29 })),
                            "英雄ACT内部5不能当成换层kind1使用");
                        warp[10] = 0;
                        var sealCaller = count == 4 ? peers[1] : leader;
                        sealCaller.Send(45, warp);
                        var sealing = All(sealCaller);
                        That(sealing.All(rows => rows.Single(packet => packet is { Kind: 0, Opcode: 29 }).Body is
                            { Length: > 19 } body && body[0] == 12 && body[1] == 1 && body[13] == 1),
                            "任一真实成员按已证ACT目标令全部真实成员进入首次封印房");
                        var sealMap = sealing[0].Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
                        That(Enumerable.Range(0, sealMap[18]).Any(index => U32(sealMap, 19 + index * 22 + 8) == 407000558) &&
                            sealing.All(rows => rows.Single(packet => packet is { Kind: 0, Opcode: 29 }).Body.SequenceEqual(sealMap)),
                            "实际成员共用原407000558封印演员计划，不建立个人实例");
                        visited.Add(new(12, 1));
                        leader.Send(45, warp);
                        That(All().All(rows => !rows.Any(packet => packet is { Kind: 0, Opcode: 29 })) && fixture.Snapshot() == fighting,
                            "封印加载屏障内迟到的其他玩家同目标请求不重建地图或提前发奖");
                        foreach (var peer in peers) { peer.Send(37, new byte[8]); All(peer); }
                        int sealAt = Enumerable.Range(0, sealMap[18]).Select(index => 19 + index * 22)
                            .Single(at => U32(sealMap, at + 8) == 407000558);
                        ushort sealKey = AntonTcpProbe.TakeFiendwarKey(leader);
                        byte[] sealReport = SpawnBody(leader, U16(sealMap, sealAt + 6), 407000557, sealKey, spawnSequence++);
                        Spawn(leader, sealReport);
                        ushort nextSealKey = AntonTcpProbe.PeekFiendwarKey(leader);
                        Spawn(leader, SpawnBody(leader, U16(sealMap, sealAt + 6), 407000557, nextSealKey, spawnSequence++));
                        ChildDeath(nextSealKey, sealMap[sealAt + 12], expected: false);
                        ChildDeath(dummyKey, map[bossAt + 12], expected: false);
                        leader.Send(45, unsupported);
                        That(All().All(rows => !rows.Any(packet => packet is { Kind: 0, Opcode: 29 })),
                            "封印房活怪状态不开放未证12,2分支");
                        if (count == 4)
                        {
                            peers[2].Send(40, [0x74, 0, 4, 1, 0, 0]);
                            That(All(peers[2]).All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 32 } &&
                                U16(packet.Body) == peers[2].Id && packet.Body[2] == 0)),
                                "原生死亡请求先在全队确认一名真实死亡成员");
                        }
                        fighting = fixture.Snapshot();
                        byte[] back = new byte[100]; back[0] = 12;
                        var returnCaller = count == 4 ? peers[3] : leader;
                        returnCaller.Send(45, back);
                        var restored = All(returnCaller);
                        That(restored.All(rows => rows.Single(packet => packet is { Kind: 0, Opcode: 29 }).Body is
                            { Length: 16 } body && body[0] == 12 && body[1] == 0 && body[13] == 0) && fixture.Snapshot() == fighting,
                            "封印未清按INCLUDE DEAD携真实死亡成员回原Boss缓存，不复活、不重建或伪造Boss死亡");
                        foreach (var peer in peers) { peer.Send(37, new byte[8]); All(peer); }
                        ChildDeath(dummyKey, map[bossAt + 12], scripted: true);
                        ChildDeath(dummyKey, map[bossAt + 12], scripted: true);
                        leader.Send(45, warp);
                        That(All().All(rows => rows.Single(packet => packet is { Kind: 0, Opcode: 29 }).Body is
                            { Length: 16 } body && body[0] == 12 && body[1] == 1 && body[13] == 0),
                            "活动态557随封印房缓存保留，第二次往返仍使用原地图和父key");
                        foreach (var peer in peers) { peer.Send(37, new byte[8]); All(peer); }
                        Spawn(leader, sealReport);
                        ChildDeath(sealKey, sealMap[sealAt + 12]);
                        ChildDeath(sealKey, sealMap[sealAt + 12]);
                        That(AntonTcpProbe.TakeFiendwarKey(leader) == nextSealKey,
                            "活槽拒绝没有消费服务端租约，跨房后仍以同一已授未用键发起合法下一轮");
                        Spawn(leader, SpawnBody(leader, U16(sealMap, sealAt + 6), 407000557, nextSealKey, spawnSequence++));
                        ChildDeath(nextSealKey, sealMap[sealAt + 12]);
                        for (int round = 0; round < 3; round++)
                        {
                            ushort key = AntonTcpProbe.TakeFiendwarKey(leader);
                            Spawn(leader, SpawnBody(leader, U16(sealMap, sealAt + 6), 407000557, key, spawnSequence++));
                            ChildDeath(key, sealMap[sealAt + 12]);
                        }
                        returnCaller.Send(45, back);
                        That(All(returnCaller).All(rows => rows.Single(packet => packet is { Kind: 0, Opcode: 29 }).Body is
                            { Length: 16 } body && body[0] == 12 && body[1] == 0 && body[13] == 0),
                            "多轮557真实登记退休后再次回原Boss，仍保留原Bosskey与唯一结算路径");
                        foreach (var peer in peers) { peer.Send(37, new byte[8]); All(peer); }
                    }
                    foreach (int offset in Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
                        .Where(offset => map[offset + 13] != 9 && map[offset + 14] == 0 && map[offset + 21] == 0).OrderBy(offset => map[offset + 13] == 3))
                    {
                        byte[] death = FormalDungeonTcpTests.Death(U16(map, offset + 6), leader.Id, map[offset + 12]);
                        if (x == 12 && U32(map, offset + 8) == 407000556) bossDeath = death;
                        leader.Send(39, death);
                        That(All().All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 38 })),
                            "英雄真实演员死亡由共享实例逐成员提交后发布");
                    }
                    if (x == 12) break;
                    byte[] move = new byte[100]; move[0] = (byte)(x + 1);
                    if (x == 11 && withholdLastLease)
                    {
                        string beforeGate = Assets();
                        leader.Send(45, move);
                        That(All().All(rows => !rows.Any(packet => packet is { Kind: 0, Opcode: 29 })) && Assets() == beforeGate,
                            "三人ready不能提前进入含one-shot召唤的Boss房，全员仍在原房且零持久变化");
                        withholdLastLease = false;
                        That(AntonTcpProbe.ProcessFiendwarKeyNotice(peers[3], new(0, FiendwarKeyLeaseRequests.Opcode, keyOffers[peers[3].Id])),
                            "最后成员使用本房实际offer完成有限原生候选握手");
                        All(peers[3]);
                    }
                    peers[x % peers.Count].Send(45, move);
                    var moved = All(peers[x % peers.Count]);
                    That(moved.All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 29 })),
                        "真实队员触发原Greed邻接，全队共用同一新地图");
                    map = moved[0].Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
                }
                That(visited.Count == 14 && visited.Count(point => point.Y == 0) == 13 && bossDeath is not null,
                    "实际抵达13主线房并完成活Boss封印往返后，使用原Bosskey报告407000556死亡");
                string killed = fixture.Snapshot();
                leader.Send(39, bossDeath!);
                That(All().All(rows => !rows.Any(packet => packet is { Kind: 0, Opcode: 38 or 1673 })) && fixture.Snapshot() == killed,
                    "重复英雄末怪不重复死亡、成长或发普通困难战役奖励");
                var report = DungeonSettlementProtocolTests.Report(peers.Select(peer => (peer.Id, (byte)99)).ToArray());
                foreach (var peer in peers)
                {
                    peer.Send(46, report);
                    var settled = All(peer)[peers.IndexOf(peer)];
                    That(settled.Count(packet => packet is { Kind: 0, Opcode: 34 }) == 1,
                        "每位真实成员由46提交自己唯一正式结算");
                    That(settled.Any(packet => packet is { Kind: 0, Opcode: 35 }),
                        "英雄同次结算明确发布真实通关牌，不以另一次Drain漏看结果");
                    string committed = Assets();
                    peer.Send(46, report);
                    That(All(peer)[peers.IndexOf(peer)].Any(packet => packet is { Kind: 0, Opcode: 34 }) && Assets() == committed,
                        "英雄原46报告重放仅回显结果，不重复写入资产");
                    That(fixture.Query("SELECT used FROM character_fatigue WHERE character_id=$p0", peer.Id) is [[0L]] &&
                        fixture.Query("SELECT count(*) FROM character_evildom_war_rewards WHERE character_id=$p0", peer.Id) is [[0L]],
                        "英雄结算保持无疲劳，不混普通三模式战役专用奖励账本");
                }
                foreach (var peer in peers)
                {
                    peer.Send(69); All(peer);
                    peer.Send(70);
                    That(All(peer)[peers.IndexOf(peer)].Any(packet => packet is { Kind: 1, Opcode: 70 }),
                        "英雄结果页复用实际资源支持的正式翻牌资格");
                    peer.Send(71, [0, 3]);
                    That(All(peer)[peers.IndexOf(peer)].Any(packet => packet is { Kind: 1, Opcode: 71, Body: [1, ..] }),
                        "每名英雄成员通过正式请求领取本人免费牌");
                    string claimed = Assets();
                    peer.Send(71, [0, 3]); All(peer);
                    That(Assets() == claimed, "英雄重复免费牌不再次改变角色、物品、邮件或结算账本");
                }
                leader.Send(72, [1, returnToSelection ? (byte)1 : (byte)2]);
                var returned = All();
                if (returnToSelection)
                    That(returned.All(rows => rows.Any(packet => packet is { Kind: 1, Opcode: 72, Body: [1, 1, 1] }) &&
                        rows.Any(packet => packet is { Kind: 0, Opcode: 27 })),
                        "英雄结算后全队重选地图，保留四人同一选图阶段");
                else
                {
                    That(returned.All(rows => rows.Any(packet => packet is { Kind: 1, Opcode: 72, Body: [1, 1, 2] }) &&
                        rows.Any(packet => packet is { Kind: 0, Opcode: 24 }) &&
                        rows.All(packet => packet.Opcode is not (28 or 29))) &&
                        peers.All(peer => fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[124L, 2L]]),
                        "英雄结算后全队回124等待区，不安装平行副本");
                    leader.Send(15, BitConverter.GetBytes(400000105u));
                    That(All().All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 27 })),
                        "英雄回城后可重新按真实Party入口选图");
                }
                leader.Send(16, selection);
                That(All().All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 28 }) &&
                    rows.Any(packet => packet is { Kind: 0, Opcode: 29 })) &&
                    peers.All(peer => fixture.Query("SELECT used FROM character_fatigue WHERE character_id=$p0", peer.Id) is [[0L]]),
                    "英雄返回或重选后可再次进入同一真实Party链且不扣疲劳");
                foreach (var peer in peers) { peer.Send(37, new byte[8]); All(peer); }
                foreach (var peer in peers)
                {
                    FiendwarRaidTcpTests.VerifyConsumable(fixture, peer, true, 9001, recoveryPotionId);
                    That(fixture.Query("SELECT count(*) FROM character_consumable_states WHERE character_id=$p0 AND item_id=$p1 AND (maintenance & 2)=0", peer.Id, recoveryPotionId)
                        is [[long stateCount]] && stateCount > 0, "英雄故障恢复前实际44药效需要Town边界事务清理");
                }
                string recoveryState = Assets();
                string Economy() => string.Join('|', peers.Select(peer => fixture.Snapshot(
                    "SELECT experience,gold,revive_coins,stamina_until FROM characters WHERE id=$p0", peer.Id)));
                string recoveryEconomy = Economy();
                byte[] cancel = (byte[])keyOffers[leader.Id].Clone(); cancel[4] = 5; cancel[5] = 6;
                if (count == 4)
                {
                    var revisions = peers.Select(peer => Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE id=$p0", peer.Id)[0][0])).ToArray();
                    string priorWrites = string.Join(" AND ", peers.Take(3).Select((peer, index) =>
                        $"(SELECT revision FROM characters WHERE id={peer.Id})>{revisions[index]} AND " +
                        $"NOT EXISTS(SELECT 1 FROM character_consumable_states WHERE character_id={peer.Id} AND (maintenance & 2)=0)"));
                    fixture.Sql($"""
                        CREATE TRIGGER hero_key_recovery_failure BEFORE UPDATE OF revision ON characters
                        WHEN NEW.id={peers[3].Id} AND NEW.revision>OLD.revision
                        BEGIN
                          SELECT CASE WHEN {priorWrites} THEN RAISE(ABORT,'hero-key-recovery-late-rollback')
                            ELSE RAISE(ABORT,'hero-key-recovery-order-not-proven') END;
                        END
                        """);
                    WirePacket[][] paused;
                    int eventBoundary = fixture.Events().Length;
                    try
                    {
                        leader.Send(FiendwarKeyLeaseRequests.Opcode, cancel); paused = All();
                        That(paused.All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: FiendwarKeyLeaseRequests.Opcode, Body.Length: 80 } &&
                                packet.Body[4] == 5 && packet.Body[5] == 2) && !rows.Any(packet => packet is { Kind: 0, Opcode: 24 or 29 })) &&
                            Assets() == recoveryState, "英雄恢复末成员SQL失败保留全员暂停锁并整体回滚，不宣称回城或继续生成");
                        That(SpinWait.SpinUntil(() => fixture.Events().Skip(eventBoundary).Any(row =>
                            row.GetProperty("Kind").GetString() == "fiendwar_key_recovery_failed" &&
                            row.GetProperty("Detail").GetProperty("Message").GetString()?.Contains("hero-key-recovery-late-rollback", StringComparison.Ordinal) == true),
                            TimeSpan.FromSeconds(2)), "实际SQL日志证明此前三名成员已写入后才在最后成员失败");
                        byte[] blockedMove = new byte[100]; blockedMove[0] = 1;
                        leader.Send(45, blockedMove);
                        That(All().All(rows => !rows.Any(packet => packet is { Kind: 0, Opcode: 29 })),
                            "恢复失败期间不能继续推进原Run");
                    }
                    finally { fixture.Sql("DROP TRIGGER IF EXISTS hero_key_recovery_failure"); }
                    byte[] retry = paused[3].Last(packet => packet is { Kind: 0, Opcode: FiendwarKeyLeaseRequests.Opcode } && packet.Body[4] == 5).Body;
                    peers[3].Send(FiendwarKeyLeaseRequests.Opcode, retry);
                    That(All(peers[3]).All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 24, Body: [124, ..] }) &&
                        !rows.Any(packet => packet is { Kind: 0, Opcode: 31 or 34 or 35 or 38 or 1673 })) &&
                        peers.All(peer => fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[124L, 2L]]) &&
                        Economy() == recoveryEconomy, "另一原成员精确重试后全员无罚返回124，不奖励、不额外收费或改变恢复前经济");
                }
                else
                {
                    leader.Send(FiendwarKeyLeaseRequests.Opcode, cancel);
                    That(All().All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 24, Body: [124, ..] }) &&
                        !rows.Any(packet => packet is { Kind: 0, Opcode: 31 or 34 or 35 or 38 or 1673 })) &&
                        fixture.Query("SELECT town,area FROM characters WHERE id=$p0", leader.Id) is [[124L, 2L]] &&
                        Economy() == recoveryEconomy, "单人英雄原队长通过实际键租约取消无罚退出，不额外收费或奖励");
                }
                That(peers.All(peer => fixture.Query("SELECT count(*) FROM character_consumable_states WHERE character_id=$p0 AND item_id=$p1 AND (maintenance & 2)=0", peer.Id, recoveryPotionId)
                    is [[0L]]), "英雄恢复成功后全员Town边界持续药效已在同一事务清理");
            }
            finally { foreach (var peer in peers) peer.Dispose(); }
        }
        Console.WriteLine($"PASS evildom war challenge TCP {Count - before}");
    }
}
