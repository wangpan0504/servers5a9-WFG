using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class GuildHallTcpTests
{
    public static Location FindStreet(TownCatalog towns, TownArea hall)
    {
        return Point(Route(towns, hall)[^1]);
    }

    static Location Point(TownArea area)
    {
        var bounds = area.Default.Bounds.First(region => region.Quests.Length == 0 && region.Conditions.Length == 0).Bounds;
        return new(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
    }

    static TownArea[] Route(TownCatalog towns, TownArea hall)
    {
        var queue = new Queue<TownArea[]>();
        var visited = new HashSet<(byte, byte)> { (hall.Town, hall.Area) };
        queue.Enqueue([hall]);
        while (queue.TryDequeue(out var route))
        {
            var source = route[^1];
            if (!TownCatalog.IsGuildHallMap(source.Default.Path)) return route;
            foreach (var link in source.Default.Links.Where(link => link.Gate.Door && link.Quests.Length == 0 && link.Conditions.Length == 0))
            {
                if (!towns.Areas.TryGetValue((link.Town, link.Area), out var target) || target.Level > 60 ||
                    target.Quests.Length != 0 || target.AnyQuests.Length != 0 || target.ExceptQuests.Length != 0 ||
                    target.Default.IsRoom || target.Default.Error is not null ||
                    !target.Default.Bounds.Any(region => region.Quests.Length == 0 && region.Conditions.Length == 0) ||
                    !target.Default.Links.Any(back => back.Town == source.Town && back.Area == source.Area && back.Gate.Door && back.Quests.Length == 0 && back.Conditions.Length == 0) ||
                    !visited.Add((target.Town, target.Area))) continue;
                queue.Enqueue([.. route, target]);
            }
        }
        throw new InvalidDataException("Guild hall has no verified reciprocal PVF route to an ordinary street");
    }

    public static void Run(TcpTestFixture fixture, TcpPeer master, TcpPeer peer, TcpPeer foreign,
        TownCatalog towns, TownArea hallArea, Location hall, Location street, ushort masterId, ushort peerId)
    {
        int before = Count;
        Location[] route = Route(towns, hallArea).Select(Point).ToArray();
        // 使用同一真实PVF有向门与已有连接，主动36出入；不把存档恢复当作主动进大厅的覆盖。
        for (int i = 1; i < route.Length; i++) Travel(peer, route[i - 1], route[i]);
        Drain(master); Drain(peer); Drain(foreign);
        for (int i = route.Length - 2; i >= 0; i--) Travel(peer, route[i + 1], route[i]);
        var reentered = Drain(master);
        That(reentered.Any(packet => IsActor(packet, peerId) ||
            packet is { Kind: 0, Opcode: 23, Body.Length: >= 4 } && U16(packet.Body, 0) == peerId),
            "同会成员主动重新入厅，原观察者恢复其Actor或已保留Actor的地区投影");
        Drain(peer); Drain(foreign);
        master.Send(17, Chat("HallSameGuild"));
        Bytes(master.Expect(0, 12).Body, Message("HallSameGuild"), "大厅普通聊天发送者收到原生正文");
        Bytes(peer.Expect(0, 12).Body, Message("HallSameGuild"), "同会同厅成员收到普通聊天");
        That(Drain(foreign).All(packet => packet.Opcode != 12), "同地图另一公会不收到普通聊天");
        foreign.Send(17, Chat("HallOtherGuild"));
        foreign.Expect(0, 12);
        That(Drain(master).All(packet => packet.Opcode != 12) && Drain(peer).All(packet => packet.Opcode != 12),
            "另一公会普通聊天也不能反向串入本会大厅");

        master.Send(153, [2, .. Dstr("CargoWireB")]);
        ExpectReply(master, 153, [1, 2, .. Dstr("Cargo wire"), 0, .. Dstr("CargoWireB")]);
        var kicked = ReadEvacuation(peer);
        AssertEvacuation(kicked, peerId, "被踢");
        AssertOutside(fixture, peerId, street);
        var retired = Drain(master);
        That(retired.Any(packet => packet is { Kind: 0, Opcode: 6, Body.Length: 2 } && U16(packet.Body, 0) == peerId),
            "被踢后同厅观察者收到旧Actor移除");
        RejectReentry(fixture, peer, peerId, route[^2]);
        Drain(master); Drain(foreign);

        master.Send(151, [2, .. Dstr("CargoWireB")]);
        ExpectReply(master, 151, [1]);
        That(Drain(peer).Any(packet => packet is { Kind: 0, Opcode: 147 }), "迁出者仍能在当前普通街道收到新邀请");
        peer.Send(152, [1]);
        ExpectReply(peer, 152, [1]);
        Drain(master); Drain(peer);
        var saved = fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", peerId)[0];
        var outside = new Location(Convert.ToByte(saved[0]), Convert.ToByte(saved[1]), Convert.ToUInt16(saved[2]), Convert.ToUInt16(saved[3]));
        That(outside.Town == street.Town && outside.Area == street.Area, "大厅迁出保留可真实返回的普通街道");
        for (int i = route.Length - 2; i >= 0; i--) Travel(peer, i == route.Length - 2 ? outside : route[i + 1], route[i]);
        Drain(master); Drain(peer);
        peer.Send(153, [0, .. Dstr("")]);
        ExpectReply(peer, 153, [1, 1, .. Dstr("Cargo wire"), 0, .. Dstr("CargoWireB")], out var beforeAck);
        var left = ReadEvacuation(peer, beforeAck);
        AssertEvacuation(left, peerId, "主动退会");
        AssertOutside(fixture, peerId, street);
        Drain(master); Drain(foreign);
        RejectReentry(fixture, peer, peerId, route[^2]);
        That(fixture.Query("SELECT count(*) FROM guild_members WHERE character_id=$p0", masterId) is [[1L]],
            "其他成员离会和迁出不影响会长真实会籍");
        Console.WriteLine($"PASS {Count - before} 公会大厅主动切图、普通聊天隔离、踢出及退会迁出 TCP 断言");
    }

    static void AssertOutside(TcpTestFixture fixture, ushort characterId, Location street) =>
        That(fixture.Query("SELECT count(*) FROM guild_members WHERE character_id=$p0", characterId) is [[0L]] &&
            fixture.Query("SELECT town,area FROM characters WHERE id=$p0", characterId) is [[long town, long area]] &&
            town == street.Town && area == street.Area, "迁出使用已提交空会籍，持久落点确实位于普通街道");

    static void AssertEvacuation(IEnumerable<WirePacket> packets, ushort characterId, string reason)
    {
        var rows = packets.ToArray();
        int area = Array.FindIndex(rows, packet => packet is { Kind: 0, Opcode: 23 } && packet.Body.Length >= 4 && U16(packet.Body, 0) == characterId);
        int transition = Array.FindIndex(rows, packet => packet is { Kind: 0, Opcode: 24 });
        That(area >= 0 && transition > area && rows.Any(packet => packet is { Kind: 0, Opcode: 151 }),
            reason + "后发送普通23→24切图并清会籍，不靠重选角色迁出");
    }

    static List<WirePacket> ReadEvacuation(TcpPeer peer, List<WirePacket>? received = null)
    {
        var packets = received ?? [];
        // 踢人确认与被踢连接的查重确认没有跨连接顺序；直接等待真实清会籍通知，再验证前面的23→24。
        for (int guard = 0; guard < 256; guard++)
        {
            if (packets.Any(packet => packet is { Kind: 0, Opcode: 151 }))
            {
                packets.AddRange(Drain(peer));
                return packets;
            }
            packets.Add(peer.Read());
        }
        throw new InvalidOperationException("Guild hall evacuation membership notification missing");
    }

    static void RejectReentry(TcpTestFixture fixture, TcpPeer peer, ushort characterId, Location hall)
    {
        var row = fixture.Query("SELECT town,area,x,y,revision FROM characters WHERE id=$p0", characterId)[0];
        var source = new Location(Convert.ToByte(row[0]), Convert.ToByte(row[1]), Convert.ToUInt16(row[2]), Convert.ToUInt16(row[3]));
        peer.Send(36, Area(source, hall));
        ExpectReply(peer, 36, [0, 1, 0, hall.Town, hall.Area]);
        That(fixture.Query("SELECT revision FROM characters WHERE id=$p0", characterId) is [[long revision]] && revision == Convert.ToInt64(row[4]),
            "95 CMD36失败含u16错误与目标town/area，拒绝重入不改持久版本");
        Drain(peer);
    }

    static void Travel(TcpPeer peer, Location source, Location target)
    {
        peer.Send(36, Area(source, target));
        var packets = Drain(peer);
        int area = packets.FindIndex(packet => packet is { Kind: 0, Opcode: 23 } && packet.Body.Length >= 4 && packet.Body[2] == target.Town && packet.Body[3] == target.Area);
        int transition = packets.FindIndex(packet => packet is { Kind: 0, Opcode: 24 } && packet.Body.Length >= 2 && packet.Body[0] == target.Town && packet.Body[1] == target.Area);
        That(area >= 0 && transition > area, "主动CMD36在真实路线提交后按23→24切图");
    }

    static byte[] Area(Location source, Location target) => Build(writer =>
    {
        writer.Write(target.Town); writer.Write(target.Area); writer.Write(target.X); writer.Write(target.Y);
        writer.Write((byte)5); writer.Write((ushort)source.Town); writer.Write((ushort)source.Area); writer.Write(0u); writer.Write((byte)0);
    });
    static bool IsActor(WirePacket packet, ushort id) => packet is { Kind: 0, Opcode: 2, Body.Length: > 122 } &&
        packet.Body[0] == 0 && U16(packet.Body, 1) == 1 && U16(packet.Body, 121) == id;
    static byte[] Dstr(string value) => Build(writer => { byte[] text = CharacterName.Encoding.GetBytes(value); writer.Write(text.Length); writer.Write(text); });
    static byte[] Chat(string value) => Build(writer => { writer.Write((byte)3); writer.Write((ushort)4); writer.Write(0u); writer.Write(Dstr(value)); });
    static byte[] Message(string value) => [(byte)3, 4, 0, 0, .. Dstr(value)];
    static List<WirePacket> Drain(TcpPeer peer)
    {
        peer.Send(156, Dstr("HallBarrier"));
        var packets = new List<WirePacket>();
        for (int guard = 0; guard < 256; guard++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 1, Opcode: 156 }) { Bytes(packet.Body, [1], "大厅后继查重屏障"); return packets; }
            packets.Add(packet);
        }
        throw new InvalidOperationException("Guild hall publication did not reach command barrier");
    }
    static void ExpectReply(TcpPeer peer, ushort opcode, byte[] body) => ExpectReply(peer, opcode, body, out _);
    static void ExpectReply(TcpPeer peer, ushort opcode, byte[] body, out List<WirePacket> before)
    {
        before = [];
        for (int guard = 0; guard < 256; guard++)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == opcode) { Bytes(packet.Body, body, "大厅成员操作确认"); return; }
            before.Add(packet);
        }
        throw new InvalidOperationException("Guild hall command acknowledgement missing");
    }
}
