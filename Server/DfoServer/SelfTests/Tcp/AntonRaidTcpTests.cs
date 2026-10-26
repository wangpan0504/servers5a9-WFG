using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// 独立按客户端读宽解码；十个连接达到PVF门槛，但资源缺失时仍不得假开战。
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
            for (int index = 0; index < 11; index++)
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
                is [[22L]], "十一名成员均真实持有两张团本门票，拒绝开战不能靠无票夹具掩盖扣费");
            var leader = peers[0]; var observer = peers[10];
            leader.Send(664, Build(w => { w.Write((byte)0); w.Write(Name("Anton")); w.Write(new byte[4]); }));
            var list = Until(leader, 0, 590).Body;
            uint key = U32(list, 4);
            That((key >> 16 & 255) == 19, "招募key属于实际频道19");
            Until(leader, 0, 591); Bytes(Until(leader, 1, 664).Body, Build(w => { w.Write((byte)1); w.Write(key); }), "创建成功key");
            observer.Send(667, [0, 0]); Bytes(Until(observer, 1, 667).Body, [1], "候战登记");
            bool listed = false;
            Drain(observer, packet => { if (packet.Opcode == 590) listed |= U32(packet.Body) == 1 && U32(packet.Body, 4) == key; });
            That(listed, "候战玩家取得当前频道实际招募列表");
            for (int index = 1; index < 10; index++)
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
            }
            leader.Send(666); Bytes(Until(leader, 1, 666).Body, [0, 19, 0], "十人不能绕过正式副本资源缺失");
            leader.Send(666); Bytes(Until(leader, 1, 666).Body, [0, 19, 0], "重复开战仍明确拒绝，不触发费用或场景准备");
            Drain(observer);
            That(fixture.Snapshot() == saved && Assets() == assets, "资源就绪检查及重复请求立即保持完整位置/资产/疲劳/回执不变");
            leader.Send(668, Build(w => w.Write(key)));
            Bytes(Until(leader, 1, 668).Body, [0, 19, 0], "招募成员不能伪装成正式攻坚重入");
            for (int index = 0; index < 4; index++)
            {
                leader.Send(669, Build(w => { w.Write(0u); w.Write((uint)peers[index].Id); w.Write(1u); }));
                Bytes(Until(leader, 1, 669).Body, [1], "队长编队");
                foreach (var peer in peers) Drain(peer);
            }
            foreach (byte mode in new byte[] { 0, 1, 2 })
            {
                var info = Information(observer, key, mode);
                That(info.Leader == leader.Id || mode == 1, "1361记录使用实际队长");
                if (mode != 0) That(info.Members.Length == 10 && info.Members.Count(m => m.Squad == 1) == 4, "1361成员及四人分队");
            }
            peers[1].Send(669, Build(w => { w.Write(0u); w.Write((uint)peers[4].Id); w.Write(1u); })); Drain(peers[1]);
            That(Information(observer, key, 1).Members.Count(m => m.Squad == 1) == 4, "越权编队不改变状态");
            leader.Send(669, Build(w => { w.Write(0u); w.Write((uint)peers[4].Id); w.Write(1u); })); Drain(leader);
            That(Information(observer, key, 1).Members.Count(m => m.Squad == 1) == 4, "同分队第五人拒绝");
            leader.Send(665, Build(w => w.Write(peers[9].Id)));
            Bytes(Until(peers[9], 1, 665).Body, [1, 1], "踢人回执投给被踢者");
            foreach (var peer in peers) Drain(peer);
            That(Information(observer, key, 1).Members.Length == 9, "踢人后准确人数");
            leader.Send(10, Peer(peers[9].Id, 456, true)); Until(leader, 1, 10); Until(peers[9], 0, 7);
            peers[9].Send(11, Peer(leader.Id, 456, false)); Until(peers[9], 1, 11);
            foreach (var peer in peers) Drain(peer);
            That(Information(observer, key, 1).Members.Length == 10, "队长邀请方向也可重新加入");
            That(fixture.Snapshot() == saved && Assets() == assets, "招募/编队/查询及拒绝开战不扣已有门票、不改资产、疲劳或副本账本");
            leader.Send(665, Build(w => w.Write(leader.Id))); Bytes(Until(leader, 1, 665).Body, [1, 0], "队长本人退出");
            foreach (var peer in peers) Drain(peer);
            That(Information(observer, key, 2) is { Members.Length: 9 } inherited && inherited.Leader == peers[1].Id,
                "最早幸存成员继任，key保持不变");
            peers[1].Send(670, Build(w => { w.Write((byte)0); w.Write(Name("Next")); w.Write((byte)0); }));
            Bytes(Until(peers[1], 1, 670).Body, [1], "继任队长可以改名");
            foreach (var peer in peers) Drain(peer);
            peers[3].Send(3, [1]); Until(peers[3], 1, 3); peers[3].Closed();
            bool cleaned = false;
            for (int attempt = 0; attempt < 30 && !cleaned; attempt++)
            {
                cleaned = Information(observer, key, 1).Members.Length == 8;
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
            That(afterLeave.Leader == peers[1].Id && afterLeave.Members.Length == 7 &&
                afterLeave.Members.All(m => m.Character != peers[4].Id), "成员退出不换队长，不残留原成员");

            peers[2].Send(7); Until(peers[2], 1, 7); Until(peers[2], 0, 2);
            That(Information(observer, key, 1).Members.Length == 6, "返回选角先清理原攻坚队成员");
            peers[2].Select(); Drain(peers[2]);
            peers[2].Send(666);
            Bytes(Until(peers[2], 1, 666).Body, [0, 19, 0], "重新选角不继承旧攻坚队或开战资格");

            // 真正关闭socket后等待幸存者的完整成员通知，以可观测事件代替固定休眠。
            Drain(peers[1]);
            peers[5].Dispose();
            bool disconnected = false;
            for (int at = 0; at < 256 && !disconnected; at++)
            {
                var packet = peers[1].Read(); Validate(packet);
                disconnected = packet is { Opcode: 591 } && U32(packet.Body, 4) == 3 &&
                    ReadMembers(packet.Body, 8).Length == 5;
            }
            That(disconnected && Information(observer, key, 1).Members.All(m => m.Character != peers[5].Id),
                "异常断线释放原成员并向幸存者发布完整名单");

            Drain(peers[6]);
            string ordinaryReturn = fixture.Snapshot("SELECT gold,level,return_town,return_area FROM characters WHERE id=$p0", peers[6].Id);
            peers[6].Send(3, [1]); Until(peers[6], 1, 3); peers[6].Closed();
            That(Information(observer, key, 1).Members.Length == 4, "离开安徒恩频道释放原成员");
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
                That(fixture.Snapshot() == persisted && Information(observer, key, 1).Members.Length == 4,
                    "再次登录不复活旧攻坚队成员，角色持久状态保持一致");
            }
            That(Assets() == assets, "退队、重选、断线及跨频道重登保留完整物品UID/核心、疲劳和副本账本");
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
        await fixture.StopAsync();
        var starts = fixture.Events().Where(e => e.GetProperty("Kind").GetString() == "raid_start_request")
            .Select(e => e.GetProperty("Detail")).Where(e => e.GetProperty("Reason").GetString() == "formal-raid-dungeon-index-missing").ToArray();
        That(starts.Length == 2 && starts.All(e => e.GetProperty("Error").GetInt32() == 19 &&
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
        {
            That(U32(body, at) == key, "1361嵌套记录key一致"); at += 4;
            at += 4 + checked((int)U32(body, at)); at += 16;
            leader = U16(body, at); Member(body, ref at);
        }
        (ushort Character, byte Squad)[] members = mode == 0 ? [] : ReadMembers(body, at);
        if (mode == 0) That(at == body.Length, "1361 mode0没有多余成员尾部");
        return (leader, members);
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
