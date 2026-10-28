using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// C2S664 CREATE_RAID：魔界大战频道 41（type 0x36，客户端按 01DC69B0 落到团本类型 6，raid.lst 没有 6）不按团本建队，
// ACK664 一律失败 0x13；1361 查类型 6 无记录回失败；667 类型 6 静默不处理；666 无记录仍回失败。
// 魔界大战开战走 67/2 芮兹的 C2S 1652 START_EVILDOM_WAR；无队伍须明确失败，合法开战由专属TCP组覆盖。
// 普雷频道 42（01DC69B0：0x32 → 类型 3）请求类型 3 时建单人队：NOTI 0x24E → NOTI 0x24F op3(M=1) → ACK664 成功 key；
// 类型与频道不符回失败；LEAVE_RAID(665) 删队回 01 00，再退无回包；RAID_ENTRY_COST_INFO(658) 回 01。
// START_RAID(666) 普雷脚本最低 1 人，开战包序未恢复，有队无队都回 0x13；SET_RAID_WAITING(667) 队长回 0x13，无队登记回 01、重复回 0x12；
// MODIFY_RAID_INFO(670) 队长同类型改名：NOTI 0x24F op2 后 ACK 01，类型不符/无队回 0x13。全程不写库。
internal static class RaidCreateTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "raid-create"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        using (var peer = Open(fixture, "raidevildom", gamePort: endpoints[41], channelNumber: 41))
        {
            // 魔界大战不是团本：建队走 67/1 伊奇的 SET_PARTY_INFO(kind=5)，raid.lst 没有类型 6，
            // 攻坚队页签发来的 CREATE_RAID 一律拒绝；开战是 67/2 芮兹的 C2S 1652。
            peer.Create("Raid41");
            peer.Select();
            string saved = fixture.Snapshot();
            peer.Send(664, Convert.FromHexString("060300000031323300000007"));
            peer.Expect(1, 664, [0, 0x13, 0]);
            // 类型 6 没有记录可查：1361 回失败体 00 + 错误 21 + mode + key。
            peer.Send(1361, Build(writer => { writer.Write((byte)1); writer.Write(0u); }));
            var info = peer.Expect(1, 1361).Body;
            That(info.Length == 8 && info[0] == 0 && U16(info, 1) == 21 && info[3] == 1 && U32(info, 4) == 0,
                "evildom 1361 类型 6 无记录：00 + 21 + mode + key");
            // 类型 6 的候战登记没有目录定义，静默不处理。
            peer.Send(667, [0, 6]);
            peer.Barrier();
            // 没有攻坚队记录，START_RAID 仍然明确拒绝。
            peer.Send(666, []);
            peer.Expect(1, 666, [0, 0x13, 0]);
            // 此角色没有队伍，芮兹开战与重复请求都必须拒绝，不能伪造成功。
            peer.Send(1652, []);
            peer.Expect(1, 1652, [0, 0, 0]);
            peer.Send(1652, []);
            peer.Expect(1, 1652, [0, 0, 0]);
            peer.Barrier();
            That(fixture.Snapshot() == saved, "evildom 664/1361/667/666 与无队1652拒绝均不写库");
        }
        using (var peer = Open(fixture, "raidprey", gamePort: endpoints[42], channelNumber: 42))
        {
            peer.Create("Raid42");
            var selected = peer.Select();
            ushort id = U16(selected.First(packet => packet.Kind == 1 && packet.Opcode == 4).Body, 9);
            string saved = fixture.Snapshot();
            peer.Send(664, Convert.FromHexString("000300000031323300000007"));
            peer.Expect(1, 664, [0, 0x13, 0]);
            peer.Send(664, Convert.FromHexString("030300000031323300000007"));
            var list = peer.Expect(0, 590).Body;
            uint key = U32(list, 4);
            That(U32(list) == 1 && (key >> 16 & 0xFF) == 42 && (key & 0xFFFF) == 1 && U32(list, 8) == 3 &&
                list.AsSpan(12, 3).SequenceEqual("123"u8) && list[15] == 3 && U16(list, 31) == id && list[^1] == 0,
                "0x24E：key 频道 42/low16 1、标题 123、+0x24 类型 3、队长角色号、尾 0");
            var open = peer.Expect(0, 591).Body;
            That(U32(open) == key && U32(open, 4) == 3 && open[8] == 1 && U16(open, 9) == id && U32(open, 12) == (uint)NameWidth("Raid42"),
                "0x24F op3 M=1：队长成员块（角色号 + 名字）");
            var ack = peer.Expect(1, 664).Body;
            That(ack.Length == 5 && ack[0] == 1 && U32(ack, 1) == key, "ACK664 成功 = 01 + 同一 key");
            peer.Send(658, [1]);
            peer.Expect(0, 598, new byte[4]);
            peer.Expect(1, 658, [1]);
            peer.Send(669, Build(writer => { writer.Write(2u); writer.Write((uint)id); writer.Write(11u); }));
            peer.Barrier(packet => That(packet.Kind == 0 && packet.Opcode is 2 or 3 or 4 or 6,
                "类型3的operation2只恢复了客户端槽编码，未闭合服务端合同不能回成功或修改团记录"));
            peer.Send(666, []);
            peer.Expect(1, 666, [0, 0x13, 0]);
            peer.Send(667, [0, 3]);
            peer.Expect(1, 667, [0, 0x13, 0]);
            peer.Send(666, []);
            peer.Expect(1, 666, [0, 0x13, 0]);
            peer.Send(670, Convert.FromHexString("03030000006162630" + "0"));
            var modified = peer.Expect(0, 591).Body;
            That(U32(modified) == key && U32(modified, 4) == 2 && U32(modified, 8) == key && U32(modified, 12) == 3 &&
                modified.AsSpan(16, 3).SequenceEqual("abc"u8) && modified[19] == 3,
                "MODIFY_RAID_INFO：NOTI 0x24F op2 = key + 2 + 整条记录（新标题 abc、类型 3）");
            peer.Expect(1, 670, [1]);
            peer.Send(670, Convert.FromHexString("02030000006162630" + "0"));
            peer.Expect(1, 670, [0, 0x13, 0]);
            peer.Send(665, [(byte)id, (byte)(id >> 8)]);
            peer.Expect(1, 665, [1, 0]);
            peer.Send(665, [(byte)id, (byte)(id >> 8)]);
            peer.Send(666, []);
            peer.Expect(1, 666, [0, 0x13, 0]);
            peer.Send(670, Convert.FromHexString("03030000006162630" + "0"));
            peer.Expect(1, 670, [0, 0x13, 0]);
            peer.Send(667, [0, 3]);
            peer.Expect(1, 667, [1]);
            peer.Barrier(packet => That(packet.Kind == 0 && packet.Opcode is 590 or 592 or 593,
                "待机成功发布已证招募和候战列表，不冒充正式开战"));
            peer.Send(667, [0, 3]);
            peer.Expect(1, 667, [0, 0x12, 0]);
            peer.Barrier();
            That(fixture.Snapshot() == saved, "频道 42 建队/退队不写库，重复退出无回包");
        }
    }
}
