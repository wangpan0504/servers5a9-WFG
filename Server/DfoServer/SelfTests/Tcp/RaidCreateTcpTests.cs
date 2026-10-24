using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// C2S664 CREATE_RAID：魔界大战频道 41（实机包，团本类型 6，raid.lst 无 6）回 ACK664 失败 0x13；
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
            peer.Create("Raid41");
            peer.Select();
            string saved = fixture.Snapshot();
            peer.Send(664, Convert.FromHexString("060300000031323300000007"));
            peer.Expect(1, 664, [0, 0x13, 0]);
            peer.Barrier();
            That(fixture.Snapshot() == saved, "频道 41 CREATE_RAID 类型 6 回 ACK664 失败 0x13（dstr 32060），不写库");
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
            peer.Expect(1, 658, [1]);
            peer.Send(666, []);
            peer.Expect(1, 666, [0, 0x13, 0]);
            peer.Send(667, [0, 3]);
            peer.Expect(1, 667, [0, 0x13, 0]);
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
            peer.Send(667, [0, 3]);
            peer.Expect(1, 667, [0, 0x12, 0]);
            peer.Barrier();
            That(fixture.Snapshot() == saved, "频道 42 建队/退队不写库，重复退出无回包");
        }
    }
}
