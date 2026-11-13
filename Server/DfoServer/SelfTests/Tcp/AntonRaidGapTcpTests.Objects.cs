using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidGapTcpTests
{
    // 复用双人真实Runtime及P1领奖，不新建Host；对象身份来自本次29、21及23，Last独立走662。
    static async Task VerifyBatteryNativeAsync(TcpTestFixture fixture, TcpPeer leader, TcpPeer member,
        RaidDungeonCatalog catalog)
    {
        var phase = AntonTcpProbe.Barrier(leader, "双人对象链进入P2");
        AntonTcpProbe.Barrier(member, "双人对象链队员进入P2");
        That(phase.Any(packet => packet is { Opcode: 587, Body: [2, 1] }), "自然休息和准备后进入P2再验证对象链");
        leader.Send(15, BitConverter.GetBytes(218u));
        That(AntonTcpProbe.Barrier(leader, "对象链打开218").Any(packet => packet is { Kind: 1, Opcode: 15, Body: [1] }),
            "双人对象测试沿正式15打开218");
        AntonTcpProbe.Barrier(member, "对象链队员选图");
        byte[] select = AntonTcpProbe.LiveSelect210.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(select, 218);
        leader.Send(16, select);
        var entered = AntonTcpProbe.Barrier(leader, "对象链正式218入场");
        var witnessed = AntonTcpProbe.Barrier(member, "对象链队员正式入场");
        byte[] information = entered.Single(packet => packet is { Kind: 0, Opcode: 28 }).Body;
        byte[] map = entered.Last(packet => packet is { Kind: 0, Opcode: 29 }).Body;
        SameRoom(map, witnessed.Last(packet => packet is { Kind: 0, Opcode: 29 }).Body, null, "双人共享本次218起点");
        That(information[12] == 0 && information[13] == 2 && information[14] == 5,
            "真实28声明小电池五选八及独立终末两组，测试只使用本次抽中的首个电池格");
        var goal = new DungeonPoint(information[15], information[16]);
        var path = AntonTcpProbe.Route(catalog, 218, information[7], map[0], map[1],
            checked((byte)goal.X), checked((byte)goal.Y), avoidBossRooms: true);
        var pathReport = new AntonTcpProbe.AntonProbeReport();
        var memberPathReport = new AntonTcpProbe.AntonProbeReport();
        Load();
        foreach (var next in path.Skip(1))
        {
            AntonTcpProbe.ClearRoom(leader, map, pathReport, out _, out _, false, member, memberPathReport);
            var move = new byte[100]; move[0] = checked((byte)next.X); move[1] = checked((byte)next.Y);
            leader.Send(45, move);
            map = AntonTcpProbe.Barrier(leader, "对象链沿45走到电池格").Last(packet => packet is { Kind: 0, Opcode: 29 }).Body;
            var otherMap = AntonTcpProbe.Barrier(member, "对象链队员迁图").Last(packet => packet is { Kind: 0, Opcode: 29 }).Body;
            SameRoom(map, otherMap, null, "Native观察前全员正式同房");
            Load();
        }
        That(new DungeonPoint(map[0], map[1]) == goal, "Native对象消息绑定本次28中实际选择的房间");
        int objectsAt = 21 + 22 * map[18];
        That(map[objectsAt - 2] == 0 && map[objectsAt] == 1 && map[objectsAt + 1] > 0,
            "真实29无地面列表且包含当前格资源对象组");
        var objects = Enumerable.Range(0, map[objectsAt + 1]).Select(index => objectsAt + 2 + index * 20).ToArray();
        int batteryAt = objects.Single(at => U32(map, at + 8) == 18865 && U32(map, at + 16) == 1);
        int x = checked((int)U32(map, batteryAt)), y = checked((int)U32(map, batteryAt + 4));
        int port = checked((int)U32(leader.LoginEndpoint, 13 + checked((int)U32(leader.LoginEndpoint, 9))));
        uint from = U32(leader.LoginEndpoint, 5), to = U32(member.LoginEndpoint, 5);
        using var first = new TcpClient { NoDelay = true };
        using var second = new TcpClient { NoDelay = true };
        await first.ConnectAsync(IPAddress.Loopback, port);
        await second.ConnectAsync(IPAddress.Loopback, port);
        await first.GetStream().WriteAsync(ObjectOuter(from, from, [0xff]));
        await second.GetStream().WriteAsync(ObjectOuter(to, to, [0xff]));
        await NativeChannelTcpTests.Seen(fixture, "native_associated", 2);
        byte[] last = [110, 0, 0, 0, 100, 0, 0, 0, 2];
        leader.Send(662, last);
        That(AntonTcpProbe.Barrier(leader, "只有Last不能扣屏障").Where(packet => packet.Opcode == 583)
            .All(packet => ObjectEnergy(packet.Body) == 10000), "只有662不产生电池能量变化");
        AntonTcpProbe.Barrier(member, "只有Last的队员屏障");
        byte[] header = new byte[36];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), 0x111);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(5), leader.Id);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(25), checked((ushort)goal.X));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(27), checked((ushort)goal.Y));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(29), uint.MaxValue);
        header[34] = 1;
        byte[] row = new byte[51];
        BinaryPrimitives.WriteUInt32LittleEndian(row, 18865);
        BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(9), 0x21);
        BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(11), 333);
        row[21] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(22), x);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(26), y);
        byte[] spawned = ObjectRecord(21, [..header, ..row], 1);
        await Relay(spawned);
        That(AntonTcpProbe.Barrier(leader, "21绑定许可仍不等于销毁").Where(packet => packet.Opcode == 583)
            .All(packet => ObjectEnergy(packet.Body) == 10000), "21与Last齐备但缺23时不扣能量");
        AntonTcpProbe.Barrier(member, "21绑定队员屏障");
        byte[] destroyed = ObjectRecord(23, [0x21, 0, 77, 1, checked((byte)goal.X), checked((byte)goal.Y)], 2);
        await Relay(destroyed);
        foreach (var peer in new[] { leader, member })
            That(AntonTcpProbe.Barrier(peer, "21加23加Last真实业务发布").Any(packet => packet is { Kind: 0, Opcode: 583 } &&
                ObjectEnergy(packet.Body) == 9900), "双事实配对后两成员收到真实583屏障9900");
        await Relay(destroyed);
        leader.Send(662, last);
        foreach (var peer in new[] { leader, member })
            That(AntonTcpProbe.Barrier(peer, "对象销毁及Last重放").Where(packet => packet.Opcode == 583)
                .All(packet => ObjectEnergy(packet.Body) == 9900), "相同对象23和Last重放不重复扣100");
        foreach (var peer in new[] { leader, member })
        {
            peer.Send(42); AntonTcpProbe.Barrier(peer, "对象链代表路径结束回城");
        }

        void Load()
        {
            leader.Send(37, new byte[8]); member.Send(37, new byte[8]);
            AntonTcpProbe.Barrier(member, "对象链最后成员加载");
            AntonTcpProbe.Barrier(leader, "对象链全员加载完成");
        }
        async Task Relay(byte[] body)
        {
            await first.GetStream().WriteAsync(ObjectOuter(from, to, body));
            // 同一Native流后续无业务记录作为顺序屏障，避免目标写出早于源端业务发布的时序竞争。
            byte[] barrier = [1, 6, 0, 0, 0, 1, 0, 0, 0, 0xff];
            await first.GetStream().WriteAsync(ObjectOuter(from, to, barrier));
            foreach (byte[] expected in new[] { body, barrier })
            {
                byte[] head = new byte[13];
                await second.GetStream().ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                int size = U16(head, 2);
                That(size == 13 + expected.Length, "Native真实中继保留完整记录长度");
                byte[] received = new byte[size]; head.CopyTo(received, 0);
                await second.GetStream().ReadExactlyAsync(received.AsMemory(13)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Bytes(received, ObjectOuter(from, to, expected), "Native21/23真实中继保留原始字节与源目标关联");
            }
        }
    }

    static int ObjectEnergy(byte[] body)
    {
        for (int index = 0; index < body[0]; index++)
            if (BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1 + index * 8)) == 110)
                return BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(5 + index * 8));
        return -1;
    }
    static byte[] ObjectOuter(uint source, uint destination, byte[] body) => Build(writer =>
    { writer.Write((ushort)1); writer.Write(checked((ushort)(13 + body.Length))); writer.Write(source); writer.Write(destination);
        writer.Write((byte)0); writer.Write(body); });

    // 固定旋转/XOR及独立CRC向量；请求不借用生产Parser或对象Builder。
    static byte[] ObjectRecord(byte opcode, byte[] plain, uint sequence)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in plain)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) != 0 ? 0x4DB89129u : 0);
        }
        return Build(writer =>
        {
            writer.Write((byte)1); writer.Write(sequence); writer.Write(checked((ushort)(7 + plain.Length)));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)2); writer.Write((ushort)opcode); writer.Write(~crc);
            foreach (byte value in plain) { int encrypted = value ^ 0x6E; writer.Write((byte)(encrypted >> 3 | encrypted << 5)); }
        });
    }
}
