using System.Buffers.Binary;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidGapTcpTests
{
    // 复用既有单人全流程Host；不建立Native连接，覆盖无peer时本地事件的正式游戏协议接线。
    static bool VerifyBatteryObserver(TcpPeer peer, RaidDungeonCatalog catalog)
    {
        peer.Send(15, BitConverter.GetBytes(218u));
        That(AntonTcpProbe.Barrier(peer, "单人观测打开218").Any(packet => packet is { Kind: 1, Opcode: 15, Body: [1] }),
            "单人对象观测沿正式15准入");
        byte[] select = AntonTcpProbe.LiveSelect210.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(select, 218);
        peer.Send(16, select);
        var entered = AntonTcpProbe.Barrier(peer, "单人观测正式218入场");
        byte[] information = entered.Single(packet => packet is { Kind: 0, Opcode: 28 }).Body;
        byte[] map = entered.Last(packet => packet is { Kind: 0, Opcode: 29 }).Body;
        byte[] identity = Identity(entered);
        That(entered.FindIndex(packet => packet.Opcode == 0x7FF0) < entered.FindIndex(packet => packet.Opcode == 29),
            "218即使关闭AI改写也先发原生参数MAI场景，供本地观测绑定");
        That(information[13] == 2 && information[14] == 5, "观测对象取本次28实际冻结的电池格");
        peer.Send(15, BitConverter.GetBytes(218u));
        var selectedAgain = AntonTcpProbe.Barrier(peer, "单人加载中重复15");
        That(selectedAgain.Any(packet => packet is { Kind: 1, Opcode: 15, Body: [1] }) &&
            selectedAgain.All(packet => packet.Opcode is not (28 or 29 or 0x7FF0)),
            "重复15仅确认原选图，不提前重放房间或消耗观测身份");
        peer.Send(16, select);
        var replayed = AntonTcpProbe.Barrier(peer, "单人加载中重复16重放原218");
        byte[] replayIdentity = Identity(replayed);
        That(replayed.Single(packet => packet is { Kind: 0, Opcode: 28 }).Body.SequenceEqual(information) &&
            replayed.Single(packet => packet is { Kind: 0, Opcode: 29 }).Body.SequenceEqual(map) &&
            replayed.All(packet => packet is not { Kind: 1, Opcode: 16 }),
            "加载重放保留原房间与冻结对象，不伪造入场ACK16");
        That(U32(replayIdentity, 4) > U32(identity, 4) &&
            replayIdentity.AsSpan(8, 16).SequenceEqual(identity.AsSpan(8, 16)) &&
            replayed.FindIndex(packet => packet.Opcode == 28) < replayed.FindIndex(packet => packet.Opcode == 0x7FF0) &&
            replayed.FindIndex(packet => packet.Opcode == 0x7FF0) < replayed.FindIndex(packet => packet.Opcode == 29),
            "加载重放按28、全新MAI序号、29重建相同房间观测身份");
        identity = replayIdentity;
        var goal = new DungeonPoint(information[15], information[16]);
        var path = AntonTcpProbe.Route(catalog, 218, information[7], map[0], map[1], checked((byte)goal.X), checked((byte)goal.Y));
        var report = new AntonTcpProbe.AntonProbeReport();
        Load();
        foreach (var next in path.Skip(1))
        {
            // 安徒恩的Boss死亡会立即判定本Run通关，之后的移动被服务端正确拒绝；若通往电池格的
            // 途中房间就有Boss，本局无法走完电池格观测，按机制如实跳过而不把它当成失败。
            if (Enumerable.Range(0, map[18]).Any(index =>
                    map[19 + index * 22 + 13] == 3 && map[19 + index * 22 + 21] != 1))
            {
                TestCheck.Skip("anton.battery-boss-on-route", "218本局通往电池格的房间含Boss，清Boss即通关，按机制跳过电池格观测");
                peer.Send(42);
                That(AntonTcpProbe.Barrier(peer, "电池观测跳过后回候战室").Any(packet =>
                    packet is { Kind: 1, Opcode: 42, Body: [1] }),
                    "电池观测因Boss跳过后先回候战室，不能把房内状态带到后续阶段夹具");
                return false;
            }
            AntonTcpProbe.ClearRoom(peer, map, report, out _, out _, false);
            byte[] move = new byte[100]; move[0] = checked((byte)next.X); move[1] = checked((byte)next.Y);
            peer.Send(45, move);
            var moved = AntonTcpProbe.Barrier(peer, "单人观测沿正式45走电池格");
            map = moved.Last(packet => packet is { Kind: 0, Opcode: 29 }).Body;
            byte[] prior = identity; identity = Identity(moved);
            That(U32(identity, 4) > U32(prior, 4), "迁图MAI序号递增，不复用旧房间令牌");
            Load();
        }
        int objectsAt = 21 + 22 * map[18];
        That(map[objectsAt - 2] == 0 && map[objectsAt] == 1, "单人所选电池格有实际资源物件");
        int batteryAt = Enumerable.Range(0, map[objectsAt + 1]).Select(index => objectsAt + 2 + index * 20)
            .Single(at => U32(map, at + 8) == 18865 && U32(map, at + 16) == 1);
        byte[] header = new byte[36];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), 0x111);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(5), peer.Id);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(25), checked((ushort)goal.X));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(27), checked((ushort)goal.Y));
        header[34] = 1;
        byte[] row = new byte[51];
        BinaryPrimitives.WriteUInt32LittleEndian(row, 18865);
        BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(9), 0x21);
        BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(11), 334);
        row[21] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(22), U32(map, batteryAt));
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(26), U32(map, batteryAt + 4));
        byte[] spawn = [..header, ..row];
        byte[] death = [0x21, 0, 78, 1, checked((byte)goal.X), checked((byte)goal.Y)];
        byte[] last = [110, 0, 0, 0, 100, 0, 0, 0, 2];
        peer.Send(662, last);
        NoChange("单人只有Last不能扣能量");
        byte[] wrongRoom = Observation(100, 21, spawn);
        BinaryPrimitives.WriteUInt32LittleEndian(wrongRoom.AsSpan(12), U32(identity, 12) + 1);
        peer.Send(0x7FF4, wrongRoom);
        peer.Send(0x7FF4, Observation(101, 21, [..spawn, 0]));
        NoChange("旧场景及坏结构不能绑定许可或推进有效序号");
        peer.Send(0x7FF4, Observation(1, 21, spawn));
        NoChange("单人生成与Last缺销毁时不扣能量");
        int monsterAt = Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
            .First(at => map[at + 21] != 1 && map[at + 13] is not (3 or 9));
        peer.Send(39, FormalDungeonTcpTests.Death(U16(map, monsterAt + 6), peer.Id, map[monsterAt + 12]));
        var killed = AntonTcpProbe.Barrier(peer, "单人对象生成后真实怪物死亡提交");
        That(killed.Any(packet => packet is { Kind: 0, Opcode: 38 }), "房内真实死亡已提交后观测仍绑定原加载身份");
        peer.Send(0x7FF4, Observation(2, 23, death));
        var changed = AntonTcpProbe.Barrier(peer, "单人无Native连接三事实配对");
        That(changed.Any(packet => packet is { Kind: 0, Opcode: 583 } && ObjectEnergy(packet.Body) == 9900) &&
            changed.All(packet => packet.Opcode is not (0x7FF4 or 29)),
            "单人观测复用真实许可扣100并发布583，不自回送生成或私有成功ACK");
        peer.Send(0x7FF4, Observation(2, 23, death)); peer.Send(662, last);
        That(AntonTcpProbe.Barrier(peer, "单人重复销毁和Last").Where(packet => packet.Opcode == 583)
            .All(packet => ObjectEnergy(packet.Body) == 9900), "单人重复观测及Last不二次扣减");
        peer.Send(42); AntonTcpProbe.Barrier(peer, "单人观测完成回候战室");
        peer.Send(0x7FF4, Observation(3, 23, death));
        That(AntonTcpProbe.Barrier(peer, "回城后的旧单人观测").All(packet => packet.Opcode != 0x7FF4),
            "回城旧观测不伪造成功且连接保留");
        return true;

        void Load() { peer.Send(37, new byte[8]); AntonTcpProbe.Barrier(peer, "单人观测房间加载完成"); }
        static byte[] Identity(List<WirePacket> packets) => packets.Single(packet => packet is { Kind: 0, Opcode: 0x7FF0 }).Body;
        void NoChange(string action) => That(AntonTcpProbe.Barrier(peer, action).Where(packet => packet.Opcode == 583)
            .All(packet => ObjectEnergy(packet.Body) == 10000), action);
        byte[] Observation(uint sequence, uint opcode, byte[] plain) => Build(writer =>
        {
            writer.Write(0x314F4E41u); writer.Write(U32(identity, 4)); writer.Write(U32(identity, 8));
            writer.Write(U32(identity, 12)); writer.Write(U32(identity, 16)); writer.Write(U32(identity, 20));
            writer.Write(sequence); writer.Write(opcode); writer.Write((uint)plain.Length); writer.Write(plain);
        });
    }
}
