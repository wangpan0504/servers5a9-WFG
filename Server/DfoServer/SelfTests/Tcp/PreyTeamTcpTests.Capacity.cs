using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PreyTeamTcpTests
{
    static void SideCapacity(TcpTestFixture fixture, TcpPeer[] peers, TownCatalog towns, uint key, int[] positions)
    {
        TcpPeer[] left = [peers[1], peers[0], peers[2], peers[3]];
        ushort party = ushort.MaxValue;
        for (int size = 1; size <= left.Length; size++)
        {
            var entered = Cross(fixture, left[size - 1], towns, 4);
            if (size == 1)
                That(entered.All(row => row is not { Kind: 0, Opcode: 9 }), "侧区首人不建立单人队伍");
            else
            {
                var expected = left.Take(size).Select((peer, slot) => (checked((byte)slot), peer.Id)).ToArray();
                ushort current = BoardingParty(entered, 0, expected);
                if (size == 2) party = current;
                That(current == party && left.Take(size - 1).All(peer =>
                    BoardingParty(Read(peer, "capacity member added"), 0, expected) == party),
                    "左侧四个名额按实际到达序保持同一跨标记队伍");
            }
        }
        Read(peers[4], "before full side request");
        string assets = Assets();
        string fifth = fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", peers[4].Id);
        peers[4].Send(36, SideRequest(peers[4], 4));
        var denied = Read(peers[4], "fifth side member denied");
        Ack(denied, 36, [0, 1, 0, 59, 4], "同侧四人后第五人收到原静默切图失败");
        That(denied.All(row => row.Opcode != (ushort)DfoServer.Network.NotiPacketType.AAS_TIPS &&
                row is not { Kind: 0, Opcode: 9 or 24 } &&
                (row is not { Kind: 0, Opcode: 23 } || U16(row.Body) != peers[4].Id)) &&
            Position(fixture, peers[4]) is { Town: 59, Area: 3 } &&
            fifth == fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", peers[4].Id) && Assets() == assets &&
            left.All(peer => Read(peer, "full side unchanged").All(row => row is not { Kind: 0, Opcode: 9 })),
            "第五人留在鸟背中间，不改位置revision、资产或四人队伍，不弹提示");
        That(Cross(fixture, peers[4], towns, 5).All(row => row is not { Kind: 0, Opcode: 9 }),
            "左侧满员不占用右侧名额，右侧独自进入仍不组队");
        Cross(fixture, peers[4], towns, 3);

        Cross(fixture, peers[3], towns, 3);
        var three = left.Take(3).Select((peer, slot) => (checked((byte)slot), peer.Id)).ToArray();
        That(left.Take(3).All(peer => BoardingParty(Read(peer, "side vacancy"), 0, three) == party),
            "一名成员回中间后原队保留其余三人的到达顺序");
        var filled = new[] { peers[1], peers[0], peers[2], peers[4] };
        var four = filled.Select((peer, slot) => (checked((byte)slot), peer.Id)).ToArray();
        That(BoardingParty(Cross(fixture, peers[4], towns, 4), 0, four) == party &&
            filled.Take(3).All(peer => BoardingParty(Read(peer, "side vacancy filled"), 0, four) == party),
            "名额释放后原第五人可进入，并排在原三名队员之后");
        foreach (var peer in filled.Reverse()) Cross(fixture, peer, towns, 3);
        foreach (var peer in peers) Read(peer, "all back on middle");

        foreach (var peer in peers.Take(3)) Cross(fixture, peer, towns, 4);
        foreach (var peer in peers) Read(peer, "before last side slot race");
        // 两个连接不插入屏障地竞争最后名额；预留必须覆盖各自位置SQL尚未完成的窗口。
        var requests = peers.Skip(3).Select(peer => SideRequest(peer, 4)).ToArray();
        peers[3].Send(36, requests[0]);
        peers[4].Send(36, requests[1]);
        var racing = peers.Skip(3).Select(peer => Read(peer, "last side slot race")).ToArray();
        int admitted = Enumerable.Range(3, 2).Single(index => Position(fixture, peers[index]).Area == 4);
        int rejected = admitted == 3 ? 4 : 3;
        That(peers.Count(peer => Position(fixture, peer).Area == 4) == 4 &&
            Position(fixture, peers[rejected]) is { Town: 59, Area: 3 } && Assets() == assets,
            "并发竞争最后名额仅一人落位，另一人仍在中间且不改资产");
        Ack(racing[rejected - 3], 36, [0, 1, 0, 59, 4], "竞争失败沿原切图失败合同收尾");
        That(racing[admitted - 3].Any(row => row is { Kind: 0, Opcode: 24, Body: [59, 4, ..] }) &&
            racing[rejected - 3].All(row => row is not { Kind: 0, Opcode: 9 or 24 }),
            "只有成功预留的连接收到侧区迁城和真实组队");
        var roster = ReadRoster(peers[0], key);
        That(peers.Select((peer, index) => roster[peer.Id].Position == positions[index] &&
            roster[peer.Id].Squad == positions[index] / 10).All(value => value),
            "满员、补位和竞争不改变整局五人编队标记");

        byte[] SideRequest(TcpPeer peer, byte area)
        {
            var from = Position(fixture, peer);
            var target = new Location(59, area, 0, 0);
            var landing = towns.Entrance(from, target, EnterQuests) ?? towns.Spawn(target, EnterQuests)
                ?? throw new InvalidOperationException("普雷侧区真实门缺少可站落点");
            return Area(59, area, landing.X, landing.Y, from.Town, from.Area);
        }
        string Assets() => fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY character_id,instance_id");
    }
}
