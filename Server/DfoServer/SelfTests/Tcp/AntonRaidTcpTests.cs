using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// 独立按客户端读宽解码；覆盖最低十人与满团五个分队，未闭合成功链时仍不得假开战。
internal static class AntonRaidTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "anton-raid"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        List<TcpPeer> peers = [];
        try
        {
            for (int index = 0; index < 21; index++)
            {
                string account = "antonmember" + index;
                var point = new Location(38, 0, 629, 196);
                using (var owner = owners.Acquire(account, () => { }))
                {
                    var candidate = CharacterCreation.Prepare("AntM" + index, resources.Characters.Get(0), point, point, resources.Learning.Get);
                    var learning = SkillPointLedger.Synchronize(0, 0, 85, candidate.InitialLearning with
                        { Experience = resources.Growth.GetLevelStartExperience(85) }, resources.Learning);
                    var created = repository.Create(owner, candidate with
                        { Level = 85, AcceptedStoryLevel = 85, CompletedQuests = [12829, 3779], InitialLearning = learning });
                    var items = resources.ItemResources;
                    var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
                    if (index < 20)
                        InventoryService.Commit(repository, owner, created, inventory.Grant(created, [new(10096296, 2)]));
                }
                var peer = Open(fixture, account, gamePort: endpoints[19], channelNumber: 19);
                peers.Add(peer); peer.Select();
            }
            foreach (var peer in peers) Drain(peer);
            string saved = fixture.Snapshot();
            string Assets() => string.Join("\n", new[] { "character_inventory_items", "character_dungeon_state",
                "character_dungeon_loot_receipts", "character_dungeon_growth_receipts", "character_fatigue" }
                .Select(table => fixture.Snapshot($"SELECT * FROM {table} ORDER BY 1,2")));
            string assets = Assets();
            That(fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10096296")
                is [[40L]], "二十名参团角色均真实持有两张门票，团外观察者无票，拒绝不能靠全团无票掩盖扣费");
            var leader = peers[0]; var observer = peers[20];
            leader.Send(664, Build(w => { w.Write((byte)0); w.Write(Name("Anton")); w.Write(new byte[4]); }));
            var list = Until(leader, 0, 590).Body;
            uint key = U32(list, 4);
            That((key >> 16 & 255) == 19, "招募key属于实际频道19");
            Until(leader, 0, 591); Bytes(Until(leader, 1, 664).Body, Build(w => { w.Write((byte)1); w.Write(key); }), "创建成功key");
            observer.Send(667, [0, 0]); Bytes(Until(observer, 1, 667).Body, [1], "候战登记");
            bool listed = false;
            Drain(observer, packet => { if (packet.Opcode == 590) listed |= U32(packet.Body) == 1 && U32(packet.Body, 4) == key; });
            That(listed, "候战玩家取得当前频道实际招募列表");
            for (int index = 1; index < 20; index++)
            {
                var entrant = peers[index];
                entrant.Send(667, [0, 0]); Bytes(Until(entrant, 1, 667).Body, [1], "入队前登记候战"); Drain(entrant);
                entrant.Send(10, Peer(leader.Id, 123, true)); Bytes(Until(entrant, 1, 10).Body, [1], "mode10申请已提交");
                var invite = Until(leader, 0, 7).Body;
                That(invite.Length == 13 && U16(invite) == entrant.Id && invite[2] == 10 && U32(invite, 3) == 123,
                    "队长收到原生mode10及完整三个u16尾字段");
                leader.Send(11, Peer(entrant.Id, 123, false));
                Bytes(Until(leader, 1, 11).Body, Build(w => { w.Write((byte)1); w.Write(entrant.Id); w.Write((byte)10); }),
                    "ACK11成功只带source角色和mode");
                bool complete = false, removed = false;
                foreach (var peer in peers)
                    Drain(peer, packet =>
                    {
                        if (ReferenceEquals(peer, entrant) && packet.Opcode == 591 && U32(packet.Body, 4) == 3)
                            complete |= ReadMembers(packet.Body, 8).Length == index + 1;
                        if (ReferenceEquals(peer, observer) && packet.Opcode == 593)
                            removed |= packet.Body.Length == 3 && packet.Body[0] == 1 && U16(packet.Body, 1) == entrant.Id;
                    });
                That(complete && removed, "新成员收到完整成员表，候战订阅者收到移除");
                if (index == 8)
                {
                    leader.Send(666); Bytes(Until(leader, 1, 666).Body, [0, 22, 0], "九人不足十人门槛");
                    peers[1].Send(666); Bytes(Until(peers[1], 1, 666).Body, [0, 8, 0], "非队长优先拒绝，不被人数或资源原因覆盖");
                }
                if (index == 9)
                {
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        leader.Send(666); Bytes(Until(leader, 1, 666).Body, [0, 19, 0], "十人达到人数门槛仍拒绝，重复请求不启动正式流程");
                    }
                    foreach (var peer in peers) Drain(peer);
                    That(fixture.Snapshot() == saved && Assets() == assets, "十人重复拒绝后，全员无准备/阶段/迁移通知且持久状态不变");
                }
            }
            Drain(observer);
            That(fixture.Snapshot() == saved && Assets() == assets, "扩至二十人仍保持完整位置/资产/疲劳/回执不变");
            leader.Send(668, Build(w => w.Write(key)));
            Bytes(Until(leader, 1, 668).Body, [0, 19, 0], "招募成员不能伪装成正式攻坚重入");
            // 660 的两字节读宽已证；招募成员和团外玩家均无正式奖励资格，不能借重复选牌进入成功链。
            var beforeReward = Information(observer, key, 1).Members;
            foreach (var requester in new[] { leader, observer })
            {
                foreach (byte selection in new byte[] { 0, 1, 2, 5 })
                {
                    for (int repeat = 0; repeat < 2; repeat++)
                    {
                        requester.Send(660, [selection, 0]);
                        Drain(requester); // 只接受既有旁路通知；任何领奖ACK或阶段/奖励通知均失败。
                    }
                }
                requester.Send(695, new byte[5]);
                requester.Send(695, Build(w => { w.Write(0x12345678u); w.Write((byte)1); w.Write(uint.MaxValue); }));
                requester.Send(695, new byte[5]);
                Drain(requester);
                var unchanged = Information(observer, key, 1);
                That(unchanged.Members.SequenceEqual(beforeReward) &&
                    fixture.Snapshot() == saved && Assets() == assets,
                    "未开战选牌及695合成上报、重复或全零输入不改变成员、分队、位置、门票、资产、疲劳或副本回执");
            }
            for (int index = 0; index < 20; index++)
            {
                leader.Send(669, Build(w => { w.Write(0u); w.Write((uint)peers[index].Id); w.Write((uint)(index / 4 + 1)); }));
                Bytes(Until(leader, 1, 669).Body, [1], "队长编队");
                foreach (var peer in peers) Drain(peer);
            }
            var beforeCost = Information(observer, key, 2);
            foreach (var reporter in new[] { leader, peers[1], observer })
            {
                foreach (byte flag in new byte[] { 0, 1 })
                {
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        reporter.Send(658, [flag]);
                        Bytes(Until(reporter, 1, 658).Body, [1], "658已证0/1重复请求的公共ACK不证明足额、扣票、退款或正式开战");
                    }
                }
                foreach (byte[] body in new byte[][] { [], [1, 0], [2], [255] })
                {
                    reporter.Send(658, body); Drain(reporter);
                }
            }
            foreach (byte[] body in new byte[][] { [0], [0, 0, 0, 0] })
            {
                leader.Send(666, body); Drain(leader);
            }
            var afterCost = Information(observer, key, 2);
            That(beforeCost.Leader == afterCost.Leader && beforeCost.Members.SequenceEqual(afterCost.Members) &&
                fixture.Snapshot() == saved && Assets() == assets,
                "有票成员及无票团外者的658两种已证值、未知值及畸形666不改变团长、完整名册、门票、位置、疲劳或回执");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                leader.Send(666); Bytes(Until(leader, 1, 666).Body, [0, 19, 0], "二十人五个满分队仍不能绕过未闭合的开战成功链");
            }
            // 后续请求的应答是排空屏障；所有成员都检查，不能只观察队长或靠等待无包证明无副作用。
            foreach (var peer in peers) Drain(peer);
            That(fixture.Snapshot() == saved && Assets() == assets, "满团重复拒绝不扣任何成员门票、不迁移或建立副本/奖励回执");
            foreach (byte mode in new byte[] { 0, 1, 2 })
            {
                var info = Information(observer, key, mode);
                That(info.Leader == leader.Id || mode == 1, "1361记录使用实际队长");
                if (mode != 0) That(info.Members.Select(m => m.Character).SequenceEqual(peers.Take(20).Select(p => p.Id)) &&
                    info.Members.Select(m => m.Squad).SequenceEqual(Enumerable.Range(0, 20).Select(i => (byte)(i / 4 + 1))),
                    "1361完整保留二十名实际成员及五个四人分队，不把分队投影当作已建立普通Party");
            }
            peers[1].Send(669, Build(w => { w.Write(0u); w.Write((uint)peers[4].Id); w.Write(1u); })); Drain(peers[1]);
            That(Information(observer, key, 1).Members.Count(m => m.Squad == 1) == 4, "越权编队不改变状态");
            leader.Send(669, Build(w => { w.Write(0u); w.Write((uint)peers[4].Id); w.Write(1u); })); Drain(leader);
            That(Information(observer, key, 1).Members.Count(m => m.Squad == 1) == 4, "同分队第五人拒绝");
            var beforeAppointment = Information(observer, key, 2);
            foreach (var (sender, operation, target, argument) in new (TcpPeer, uint, uint, uint)[]
            {
                (peers[1], 1, peers[7].Id, 0), (leader, 1, observer.Id, 0), (leader, 1, leader.Id, 0),
                (leader, 1, peers[7].Id, 1), (leader, 1, 0x10000u + peers[7].Id, 0), (leader, 2, peers[7].Id, 0),
                (leader, 2, peers[7].Id, 11), (leader, 2, peers[7].Id, 14), (leader, 2, peers[7].Id, 21)
            })
            {
                sender.Send(669, Build(w => { w.Write(operation); w.Write(target); w.Write(argument); }));
                Drain(sender);
            }
            var afterRejectedAppointment = Information(observer, key, 2);
            That(afterRejectedAppointment.Leader == leader.Id && beforeAppointment.Members.SequenceEqual(afterRejectedAppointment.Members),
                "非法委任及类型3专用operation2槽编码不能改变安徒恩团长、成员顺序或五个分队，也不伪造成功");
            leader.Send(669, Build(w => { w.Write(1u); w.Write((uint)peers[7].Id); w.Write(0u); }));
            Bytes(Until(leader, 1, 669).Body, [1], "委任团长ACK只含公共成功标志");
            foreach (var peer in peers)
            {
                bool updated = false;
                Drain(peer, packet =>
                {
                    if (packet.Opcode != 591 || U32(packet.Body, 4) != 2) return;
                    int at = 8;
                    updated |= ReadRecord(packet.Body, ref at, key) == peers[7].Id;
                    That(at == packet.Body.Length, "委任记录通知完整消费，不另造团长通知载荷");
                });
                That(ReferenceEquals(peer, leader) || updated, "新团长、其余团员及候战观察者收到实际新团长记录");
                That(Information(peer, key, 0).Leader == peers[7].Id, "团员和候战观察者均可读取新团长");
            }
            var afterAppointment = Information(observer, key, 2);
            That(afterAppointment.Members.SequenceEqual(beforeAppointment.Members), "委任保留二十人槽序及五个满分队");
            leader.Send(669, Build(w => { w.Write(1u); w.Write((uint)peers[7].Id); w.Write(0u); })); Drain(leader);
            leader.Send(666); Bytes(Until(leader, 1, 666).Body, [0, 8, 0], "旧团长重复委任无效且开战权限立即失效");
            peers[7].Send(666); Bytes(Until(peers[7], 1, 666).Body, [0, 19, 0], "新团长也不能绕过未闭合成功链");
            peers[7].Send(669, Build(w => { w.Write(1u); w.Write((uint)leader.Id); w.Write(0u); }));
            Bytes(Until(peers[7], 1, 669).Body, [1], "新团长可以交回权限");
            foreach (var peer in peers) Drain(peer);
            That(Information(observer, key, 2).Leader == leader.Id && fixture.Snapshot() == saved && Assets() == assets,
                "委任与交回权限无迁移/阶段通知，不扣门票、不改位置资产或副本回执");
            leader.Send(665, Build(w => w.Write(peers[9].Id)));
            Bytes(Until(peers[9], 1, 665).Body, [1, 1], "踢人回执投给被踢者");
            foreach (var peer in peers) Drain(peer);
            That(Information(observer, key, 1).Members.Length == 19, "踢人后准确人数");
            leader.Send(10, Peer(peers[9].Id, 456, true)); Until(leader, 1, 10); Until(peers[9], 0, 7);
            peers[9].Send(11, Peer(leader.Id, 456, false)); Until(peers[9], 1, 11);
            foreach (var peer in peers) Drain(peer);
            That(Information(observer, key, 1).Members.Length == 20, "队长邀请方向也可重新加入");
            That(fixture.Snapshot() == saved && Assets() == assets, "招募/编队/查询及拒绝开战不扣已有门票、不改资产、疲劳或副本账本");
            leader.Send(665, Build(w => w.Write(leader.Id))); Bytes(Until(leader, 1, 665).Body, [1, 0], "队长本人退出");
            foreach (var peer in peers) Drain(peer);
            That(Information(observer, key, 2) is { Members.Length: 19 } inherited && inherited.Leader == peers[1].Id,
                "最早幸存成员继任，key保持不变");
            peers[1].Send(670, Build(w => { w.Write((byte)0); w.Write(Name("Next")); w.Write((byte)0); }));
            Bytes(Until(peers[1], 1, 670).Body, [1], "继任队长可以改名");
            foreach (var peer in peers) Drain(peer);
            peers[3].Send(3, [1]); Until(peers[3], 1, 3); peers[3].Closed();
            bool cleaned = false;
            for (int attempt = 0; attempt < 30 && !cleaned; attempt++)
            {
                cleaned = Information(observer, key, 1).Members.Length == 18;
                if (!cleaned) await Task.Delay(20);
            }
            That(cleaned, "断线清理原会话成员");
            observer.Send(1361, Build(w => { w.Write((byte)2); w.Write(key + 100); }));
            Bytes(Until(observer, 1, 1361).Body, Build(w => { w.Write((byte)0); w.Write((ushort)21); w.Write((byte)2); w.Write(key + 100); }),
                "1361缺失记录失败仍带mode/key，完整消费8字节");

            // 继续使用同一服务和成员，核对退出入口真正经过协议生命周期清理，而非只验证领域方法。
            foreach (var peer in peers.Where((_, index) => index != 3)) Drain(peer);
            peers[4].Send(665, Build(w => w.Write(peers[4].Id)));
            Bytes(Until(peers[4], 1, 665).Body, [1, 0], "普通成员可以主动离开攻坚队");
            var afterLeave = Information(observer, key, 2);
            That(afterLeave.Leader == peers[1].Id && afterLeave.Members.Length == 17 &&
                afterLeave.Members.All(m => m.Character != peers[4].Id), "成员退出不换队长，不残留原成员");

            peers[2].Send(7); Until(peers[2], 1, 7); Until(peers[2], 0, 2);
            That(Information(observer, key, 1).Members.Length == 16, "返回选角先清理原攻坚队成员");
            peers[2].Select(); Drain(peers[2]);
            peers[2].Send(666);
            Bytes(Until(peers[2], 1, 666).Body, [0, 19, 0], "重新选角不继承旧攻坚队或开战资格");
            var beforeStaleMembers = Information(observer, key, 1).Members;
            string beforeStaleReward = fixture.Snapshot();
            peers[2].Send(660, [0, 0]); Drain(peers[2]);
            That(fixture.Snapshot() == beforeStaleReward && Assets() == assets &&
                Information(observer, key, 1).Members.SequenceEqual(beforeStaleMembers),
                "重新选角后旧会话不恢复团本奖励资格、不发奖或重新加入原团");

            // 真正关闭socket后等待幸存者的完整成员通知，以可观测事件代替固定休眠。
            Drain(peers[1]);
            peers[5].Dispose();
            bool disconnected = false;
            for (int at = 0; at < 256 && !disconnected; at++)
            {
                var packet = peers[1].Read(); Validate(packet);
                disconnected = packet is { Opcode: 591 } && U32(packet.Body, 4) == 3 &&
                    ReadMembers(packet.Body, 8).Length == 15;
            }
            That(disconnected && Information(observer, key, 1).Members.All(m => m.Character != peers[5].Id),
                "异常断线释放原成员并向幸存者发布完整名单");

            using (var reconnected = Open(fixture, "antonmember5", gamePort: endpoints[19], channelNumber: 19))
            {
                reconnected.Select(); Drain(reconnected);
                var membersBeforeReconnectReward = Information(observer, key, 1).Members;
                string beforeReconnectReward = fixture.Snapshot();
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    reconnected.Send(668, Build(w => w.Write(key)));
                    Bytes(Until(reconnected, 1, 668).Body, [0, 19, 0], "真实重连后旧团key不恢复正式实例");
                    reconnected.Send(660, [0, 0]);
                    reconnected.Send(660, [2, 0]);
                    reconnected.Send(695, new byte[5]);
                    Drain(reconnected);
                }
                That(reconnected.Id == peers[5].Id && fixture.Snapshot() == beforeReconnectReward && Assets() == assets &&
                    Information(observer, key, 1).Members.SequenceEqual(membersBeforeReconnectReward),
                    "同角色新连接重复重入、选牌及全零695不恢复旧成员或奖励资格、不写资产与回执");
                reconnected.Send(3, [1]); Bytes(Until(reconnected, 1, 3).Body, [1], "重连回归正常退出"); reconnected.Closed();
            }

            Drain(peers[6]);
            string ordinaryReturn = fixture.Snapshot("SELECT gold,level,return_town,return_area FROM characters WHERE id=$p0", peers[6].Id);
            peers[6].Send(3, [1]); Until(peers[6], 1, 3); peers[6].Closed();
            That(Information(observer, key, 1).Members.Length == 14, "离开安徒恩频道释放原成员");
            byte normal = endpoints.Keys.Min();
            using (var normalPeer = Open(fixture, "antonmember6", gamePort: endpoints[normal], channelNumber: normal))
            {
                var entry = normalPeer.Select(nativeSwitch: true); normalPeer.Barrier();
                var location = entry.Last(p => p is { Kind: 0, Opcode: 24 }).Body;
                That(location[0] == 38 && location[1] == 1 &&
                    fixture.Snapshot("SELECT gold,level,return_town,return_area FROM characters WHERE id=$p0", normalPeer.Id) == ordinaryReturn,
                    "原生重连普通频道回38/1，保留普通返回地区、金币与等级");
                normalPeer.Send(3, [1]); normalPeer.Expect(1, 3, [1]); normalPeer.Closed();
            }
            string persisted = fixture.Snapshot();
            using (var relogged = Open(fixture, "antonmember6", gamePort: endpoints[normal], channelNumber: normal))
            {
                relogged.Select(); relogged.Barrier();
                That(fixture.Snapshot() == persisted && Information(observer, key, 1).Members.Length == 14,
                    "再次登录不复活旧攻坚队成员，角色持久状态保持一致");
            }
            That(Assets() == assets, "退队、重选、断线及跨频道重登保留完整物品UID/核心、疲劳和副本账本");
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
        await fixture.StopAsync();
        var starts = fixture.Events().Where(e => e.GetProperty("Kind").GetString() == "raid_start_request")
            .Select(e => e.GetProperty("Detail")).Where(e => e.GetProperty("Reason").GetString() == "formal-raid-dungeon-index-missing").ToArray();
        That(starts.Length == 5 && starts.Count(e => e.GetProperty("Members").GetInt32() == 10) == 2 &&
            starts.Count(e => e.GetProperty("Members").GetInt32() == 20) == 3 && starts.All(e => e.GetProperty("Error").GetInt32() == 19 &&
            e.GetProperty("MissingDungeons").GetInt32() == 14 && e.GetProperty("MissingMaps").GetInt32() == 72 &&
            e.GetProperty("TutorialScripts").GetInt32() == 14 && e.GetProperty("MissingItems").GetInt32() == 0 &&
            e.GetProperty("DefinitionIssues").GetInt32() == 0 && e.GetProperty("DungeonSample").GetString()!.Split(',').Length <= 8),
            "真实START日志区分主表缺项与备用脚本存在，诊断只输出有界样本");
    }

    static byte[] Peer(ushort character, uint peer, bool invite) => Build(w =>
    { w.Write(character); w.Write((byte)10); w.Write(peer); if (invite) w.Write(new byte[6]); });

    static WirePacket Until(TcpPeer peer, byte kind, ushort opcode)
    {
        for (int at = 0; at < 256; at++)
        {
            var packet = peer.Read();
            if (packet.Kind == kind && packet.Opcode == opcode) return packet;
            Validate(packet);
        }
        throw new InvalidOperationException("团本通知超过有界读取预算");
    }
    static void Drain(TcpPeer peer, Action<WirePacket>? inspect = null)
    {
        peer.Send(1278);
        for (int at = 0; at < 256; at++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 1, Opcode: 1278 }) { Bytes(packet.Body, [1], "连接后续请求仍健康"); return; }
            Validate(packet); inspect?.Invoke(packet);
        }
        throw new InvalidOperationException("团本通知超过有界排空预算");
    }
    static void Validate(WirePacket packet)
    {
        That(packet.Kind == 0 && packet.Opcode is 2 or 3 or 4 or 6 or 7 or 8 or 590 or 591 or 592 or 593,
            $"只允许已知旁路通知，实际{packet.Kind}/{packet.Opcode}");
        if (packet.Opcode == 591 && U32(packet.Body, 4) == 3) ReadMembers(packet.Body, 8);
        if (packet.Opcode == 592) That(packet.Body.Length == 2 + 3 * U16(packet.Body), "候战列表完整u16数量及条目");
        if (packet.Opcode == 593) That(packet.Body.Length == (packet.Body[0] == 0 ? 4 : 3), "候战增删字段完整");
    }
    static (ushort Leader, (ushort Character, byte Squad)[] Members) Information(TcpPeer peer, uint key, byte mode)
    {
        peer.Send(1361, Build(w => { w.Write(mode); w.Write(key); }));
        byte[] body = Until(peer, 1, 1361).Body;
        That(body[0] == 1 && body[1] == mode && U32(body, 2) == key, "1361成功前缀完整");
        int at = 6; ushort leader = 0;
        if (mode is 0 or 2)
            leader = ReadRecord(body, ref at, key);
        (ushort Character, byte Squad)[] members = mode == 0 ? [] : ReadMembers(body, at);
        if (mode == 0) That(at == body.Length, "1361 mode0没有多余成员尾部");
        return (leader, members);
    }
    static ushort ReadRecord(byte[] body, ref int at, uint key)
    {
        That(U32(body, at) == key, "嵌套记录key一致"); at += 4;
        at += 4 + checked((int)U32(body, at)); at += 16;
        ushort leader = U16(body, at); Member(body, ref at);
        return leader;
    }
    static (ushort Character, byte Squad)[] ReadMembers(byte[] body, int at)
    {
        int count = body[at++]; List<(ushort, byte)> result = [];
        for (int index = 0; index < count; index++) result.Add(Member(body, ref at));
        That(at == body.Length, "完整消费每个53B固定字段、变长名字与分队，不能混入多余字节");
        return result.ToArray();
    }
    static (ushort Character, byte Squad) Member(byte[] body, ref int at)
    {
        int start = at; ushort id = U16(body, at); at += 3;
        int name = checked((int)U32(body, at)); at += 4 + name;
        byte squad = body[at + 2]; at += 46;
        That(at - start == 53 + name && at <= body.Length, "成员53B固定字段及CP936名字读宽");
        return (id, squad);
    }
}
