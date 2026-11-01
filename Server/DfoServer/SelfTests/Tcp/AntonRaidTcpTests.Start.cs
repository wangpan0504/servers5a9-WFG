using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidTcpTests
{
    // 复用本组资源、角色及服务；重连清空旧招募身份，不再启动第二个服务或加载PVF。
    static Task VerifyStartSuccessAsync(TcpTestFixture fixture, int port, TcpPeer observer, List<TcpPeer> cleanup)
    {
        var members = Enumerable.Range(0, 20).Select(i =>
        {
            var peer = Open(fixture, "antonmember" + i, gamePort: port, channelNumber: 19);
            cleanup.Add(peer); peer.Select(); return peer;
        }).ToArray();
        foreach (var peer in members) Drain(peer);
        Drain(observer);
        var leader = members[0];
        leader.Send(664, Build(w => { w.Write((byte)0); w.Write(Name("StartOK")); w.Write(new byte[] { 0, 0, 0, 7 }); }));
        uint key = U32(Until(leader, 0, 590).Body, 4);
        Until(leader, 0, 591); Until(leader, 1, 664);
        for (int i = 1; i < members.Length; i++)
        {
            leader.Send(10, Peer(members[i].Id, 0, true)); Until(leader, 1, 10); Until(members[i], 0, 7);
            members[i].Send(11, Peer(leader.Id, 0, false)); Until(members[i], 1, 11);
            foreach (var peer in members) Drain(peer);
            Drain(observer);
        }
        for (int i = 0; i < members.Length; i++)
        {
            leader.Send(669, Build(w => { w.Write(0u); w.Write((uint)members[i].Id); w.Write((uint)(i / 4 + 1)); }));
            Bytes(Until(leader, 1, 669).Body, [1], "成功链正式分队分配");
            foreach (var peer in members) Drain(peer);
            Drain(observer);
        }
        var ids = members.Select(p => p.Id).ToArray();
        string before = fixture.Snapshot();
        string inventory = fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2");
        foreach (byte flag in new byte[] { 0, 1 })
        {
            leader.Send(658, [flag]);
            var costs = Until(leader, 0, 598).Body;
            VerifyCosts(costs, ids, 1, 2);
            Bytes(Until(leader, 1, 658).Body, [1], "658两种已证触发均完成598快照和ACK往返");
            foreach (var peer in members) Drain(peer);
        }
        That(fixture.Snapshot() == before && fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2") == inventory,
            "658按服务端库存读费用状态，不能冒充扣票命令");

        leader.Send(666);
        var frames = new List<WirePacket>[members.Length];
        for (int i = 0; i < members.Length; i++)
        {
            var packets = new List<WirePacket>(); frames[i] = packets;
            bool started = false, acknowledged = i != 0;
            for (int n = 0; n < 256 && (!started || !acknowledged); n++)
            {
                var packet = members[i].Read(); packets.Add(packet);
                if (packet is { Kind: 0, Opcode: 587 }) { Bytes(packet.Body, [2, 0], "587精确首阶段启动状态/phase"); started = true; }
                else if (packet is { Kind: 1, Opcode: 666 }) { Bytes(packet.Body, [1], "666仅公共成功标志，不追加猜测实例字段"); acknowledged = true; }
                else That(packet.Kind == 0 && packet.Opcode is 2 or 3 or 4 or 6 or 9 or 11 or 14 or 22 or 23 or 24 or 26 or 153 or 583 or 585 or 591 or 594 or 598,
                    $"开战只发送已证普通及团本通知：{packet.Kind}/{packet.Opcode}");
                if (packet is { Kind: 0, Opcode: 22 or 23 })
                    That(packet.Body.Length == (packet.Opcode == 22 ? 9 : 10) && ids.Contains(U16(packet.Body)),
                        "城镇同屏仍完整同步本团角色的位置和区域，不能遗漏已有22/23通知");
            }
            That(started && acknowledged, "每个成员完成实际开战通知链");
            int party = packets.FindIndex(p => p.Opcode == 9), prepare = packets.FindIndex(p => p.Opcode == 594);
            int area = packets.FindIndex(p => p.Opcode == 23 && U16(p.Body) == members[i].Id), transition = packets.FindIndex(p => p.Opcode == 24);
            That(party >= 0 && party < prepare && prepare < area && area < transition && transition < packets.FindIndex(p => p.Opcode == 587) &&
                packets.Single(p => p.Opcode == 594).Body.Length == 0, "完整普通Party先于空体594，候战室23/24先于587首阶段启动");
            var place = packets[area].Body;
            That(place[2] == 19 && place[3] == 2 && U16(place, 4) == 25 && U16(place, 6) == 405,
                "位置取WAITING ROOM声明19/2及真实PVF门中心25/405，不拼造副本MAP编号");
            VerifyCosts(packets.Single(p => p.Opcode == 598).Body, ids, 2, 1);
            VerifyMap(packets.Single(p => p.Opcode == 585).Body);
            That(transition < packets.FindIndex(p => p.Opcode == 585) &&
                packets.FindIndex(p => p.Opcode == 585) < packets.FindIndex(p => p.Opcode == 587),
                "二十人均在候战室迁移后、阶段演出前收到完整战况快照");
            byte[] roster = packets.Single(p => p.Opcode == 9).Body;
            That(U16(roster, 4) == U16(frames[i / 4 * 4].Single(p => p.Opcode == 9).Body, 4), "同分队共享真实普通Party编号");
            Bytes(packets.Single(p => p.Opcode == 26).Body, [0], "每分队普通Party首槽为host");
        }
        That(frames.Select(p => U16(p.Single(f => f.Opcode == 9).Body, 4)).Distinct().Count() == 5,
            "二十人生成五支普通Party，不把全团塞进四人名册");
        That(fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10096296") is [[20L]] &&
            fixture.Query("SELECT count(*) FROM characters WHERE town=19 AND area=2") is [[20L]] &&
            fixture.Query("SELECT count(DISTINCT action_name) FROM inventory_audit_log WHERE action_name LIKE 'anton_raid_start:%'") is [[1L]],
            "全团一笔持久扣票、迁移及同实例审计，不依赖通知推断SQL成功");
        string committed = fixture.Snapshot(), committedInventory = fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2");
        members[1].Send(13);
        Bytes(ReadUntil(members[1], 1, 13).Body, [0, 19, 0], "候战室普通13不能绕过攻坚队冻结名单");

        // 完整复现594多人分队消费者的C2S11：非首成员向本分队首成员发送mode0，无mode10邀请。
        for (int i = 1; i < members.Length; i++)
        {
            if (i % 4 == 0) continue;
            ushort first = members[i / 4 * 4].Id;
            members[i].Send(11, Build(w => { w.Write(first); w.Write((byte)0); w.Write(0u); }));
            Bytes(ReadUntil(members[i], 1, 11).Body, Build(w => { w.Write((byte)1); w.Write(first); w.Write((byte)0); }),
                "594自动mode0回应由既有普通处理器幂等确认");
        }
        // 两个连接先并发投递再读取；同连接流水线重复请求与成员越权不能重建实例或重复扣费。
        leader.Send(666); members[1].Send(666); leader.Send(666);
        for (int i = 0; i < 2; i++) Bytes(ReadUntil(leader, 1, 666).Body, [0, 19, 0], "战斗中重复666明确拒绝且不重开");
        Bytes(ReadUntil(members[1], 1, 666).Body, [0, 8, 0], "开战后并发成员请求仍不能取得团长权限");
        That(fixture.Snapshot() == committed && fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2") == committedInventory,
            "普通分队回应、重复开战和越权请求不重复扣费或迁移");

        leader.Send(15, new byte[4]);
        foreach (var peer in members.Take(4))
        {
            var selecting = new List<WirePacket>();
            for (int n = 0; n < 64 && !selecting.Any(p => p is { Kind: 0, Opcode: 27 }); n++) selecting.Add(peer.Read());
            // 开团时已逐项核对完整14图；选图没有状态变化，不重复下发同一列表。
            Bytes(selecting.Single(p => p.Opcode == 585).Body, [0, 0, 0, 0, 0, 0],
                "选图585只保留首字节、空变化列表与感染索引0，沿用已验证的初始地图状态");
            That(selecting.All(p => p.Opcode is not (587 or 28 or 29)), "多人分队打开选图同步地图，不重播阶段或伪造入场");
        }
        leader.Send(132);
        foreach (var peer in members.Take(4)) ReadUntil(peer, 0, 24);
        That(fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2") == committedInventory &&
            fixture.Query("SELECT count(DISTINCT action_name) FROM inventory_audit_log WHERE action_name LIKE 'anton_raid_start:%'") is [[1L]],
            "多人选图返回不重复扣票，门票审计仍属于原开战实例");
        // 132按原城镇规则保存安全返回点与revision；之后的退团断言从这次实际提交开始比较。
        committed = fixture.Snapshot();

        var leaving = members[3]; ushort originalParty = U16(frames[3].Single(p => p.Opcode == 9).Body, 4);
        leaving.Send(665, Build(w => w.Write(leaving.Id)));
        bool detached = false, left = false;
        for (int n = 0; n < 256 && !left; n++)
        {
            var packet = leaving.Read();
            if (packet is { Kind: 0, Opcode: 9 })
                detached |= packet.Body.Length == 11 && U16(packet.Body, 4) == originalParty && packet.Body[9] == 3;
            else if (packet is { Kind: 1, Opcode: 665 }) { Bytes(packet.Body, [1, 0], "665成功仍沿普通退出标志"); left = true; }
        }
        That(left && detached, "665成功前已解除实际普通分队，不能留下跨攻坚队的旧Party身份");
        leaving.Send(1361, Build(w => { w.Write((byte)1); w.Write(key); }));
        var afterLeave = ReadUntil(leaving, 1, 1361).Body;
        var remaining = ReadMembers(afterLeave, 6);
        That(afterLeave[0] == 1 && afterLeave[1] == 1 && U32(afterLeave, 2) == key && remaining.Length == 19 &&
            remaining.All(m => m.Character != leaving.Id), "退出后的真实攻坚队名册只剩十九人");
        // 01DC9C70的单人成组发送格式；退出后用原12处理器创建新普通Party，证明两套旧身份均已清除。
        leaving.Send(12, Build(w => { w.Write((byte)0); w.Write((byte)0); w.Write(Name("Party : 1")); w.Write((byte)4);
            w.Write(uint.MaxValue); w.Write((byte)5); w.Write((ushort)0); w.Write((byte)0); w.Write((byte)0); w.Write((ushort)0); w.Write((byte)7); }));
        // 退团后的目录worker也会发9；以本次26收齐创建结果，再按成员身份选自己的名册。
        var created = new List<WirePacket>();
        for (int n = 0; n < 256; n++)
        {
            var packet = leaving.Read(); created.Add(packet);
            if (packet is { Kind: 0, Opcode: 26 }) break;
        }
        var own = created.Where(packet => packet is { Kind: 0, Opcode: 9 } && packet.Body.Length >= 41 &&
                packet.Body[9] == 0 && packet.Body[12] == 0)
            .Select(packet => (packet.Body, CountAt: 41 + checked((int)U32(packet.Body, 13))))
            .Where(row => row.Body.Length > row.CountAt + 3 && row.Body[row.CountAt] > 0 &&
                U16(row.Body, row.CountAt + 2) == leaving.Id).ToArray();
        That(own.Length == 1, "原生成组必须返回唯一包含本人的完整名册，目录广播不能冒充创建结果");
        var solo = own[0].Body;
        int countAt = own[0].CountAt;
        That(U16(solo) == 1 && U16(solo, 2) == 9999 && U16(solo, 4) != originalParty && solo[9] == 0 && solo[12] == 0 &&
            solo[countAt] == 1 && solo[countAt + 1] == 0 && U16(solo, countAt + 2) == leaving.Id && solo.Length == countAt + 19,
            "退出后原生成组12成功取得新Party编号和唯一真实成员slot0");
        That(created.Last() is { Kind: 0, Opcode: 26 }, "原生成组在原收包预算内完成host通知");
        Bytes(created.Last().Body, [0], "退出后新普通单人队正确安装host");
        That(fixture.Snapshot() == committed && fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2") == committedInventory,
            "候战室退出与重新普通成组不冒充未知退款，不再修改已提交资产");
        return Task.CompletedTask;
    }

    static void VerifyMap(byte[] body)
    {
        int[] ids = [210, 211, 212, 213, 214, 215, 216, 218, 219, 220, 221, 222, 223, 224];
        That(body.Length == 76 && body[0] == 0 && body[1] == 14 && U32(body, 72) == 0,
            "585为76B完整14图列表，未知首字节保守0、无感染图索引0");
        for (int index = 0; index < ids.Length; index++)
            That(U32(body, 2 + index * 5) == ids[index] && body[6 + index * 5] == (index == 0 ? 0 : 2),
                "585数字ID及初始open/hide逐项独立核对，不替换为普通9595系列");
    }

    static WirePacket ReadUntil(TcpPeer peer, byte kind, ushort opcode)
    {
        for (int i = 0; i < 256; i++) { var packet = peer.Read(); if (packet.Kind == kind && packet.Opcode == opcode) return packet; }
        throw new InvalidOperationException("成功链超过有界读预算");
    }

    static void VerifyCosts(byte[] body, ushort[] ids, byte state, short count)
    {
        That(U32(body) == ids.Length && body.Length == 4 + 5 * ids.Length, "598人数和5字节完整条目");
        for (int i = 0; i < ids.Length; i++)
            That(U16(body, 4 + 5 * i) == ids[i] && body[6 + 5 * i] == state && U16(body, 7 + 5 * i) == count,
                "598逐成员角色号、状态与有符号库存数量均取实际提交结果");
    }
}
