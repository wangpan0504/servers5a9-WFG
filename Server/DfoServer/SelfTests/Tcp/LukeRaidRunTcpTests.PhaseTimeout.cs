using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class LukeRaidRunTcpTests
{
    internal static void PhaseTimeout(TcpTestFixture fixture, TcpPeer[] peers, LukeRaidClockTests.Clock clock)
    {
        var first = peers[..4]; var second = peers[4..];
        Enter(first, first[0], 5054); Load(first);
        ReadOtherPartyDeparture(first, second);
        Enter(second, second[0], 5055); Load(second);
        foreach (var peer in peers) Drain(peer);
        const string ownedItems = "SELECT character_id,item_uid,item_core FROM character_inventory_items WHERE item_uid NOT IN (SELECT item_uid FROM mailbox_attachments) ORDER BY character_id,item_uid";
        string assets = fixture.Snapshot(ownedItems);
        string coins = fixture.Snapshot("SELECT id,revive_coins FROM characters ORDER BY id");
        clock.Advance(2099);
        foreach (var peer in peers) That(!Drain(peer).Any(packet => packet.Opcode == 24), "deadline前不强退任一分队");
        clock.Advance(1);
        foreach (var peer in peers)
        {
            var frames = new List<WirePacket>();
            for (int count = 0; count < 256; count++)
            {
                var packet = peer.Read(); Validate(packet); frames.Add(packet);
                if (packet is { Kind: 0, Opcode: 24 }) break;
            }
            That(frames.Any(packet => packet is { Kind: 0, Opcode: 24 }) &&
                !frames.Any(packet => packet.Opcode is 587 or 600 or 601),
                "真实Runtime阶段到期两队八人自动回城，不猜失败587/奖励600/601");
            Drain(peer);
            peer.Send(660, [0, 1]);
            That(Drain(peer, phaseResults: true).Single(packet => packet is { Kind: 1, Opcode: 660 }).Body[0] == 0, "超时不提供新领奖资格");
        }
        That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE phase0_receipt IS NOT NULL OR phase1_receipt IS NOT NULL OR state=2") is [[0L]],
            "阶段失败无成功资格或周clear行");
        That(fixture.Snapshot(ownedItems) == assets &&
            fixture.Snapshot("SELECT id,revive_coins FROM characters ORDER BY id") == coins,
            "自动失败退场不再次扣票/扣币，退票附件不改原背包或发通关奖励");
        foreach (var peer in peers)
            That(fixture.Query("SELECT count(*) FROM mailbox_messages WHERE recipient_id=$p0 AND delivery_key LIKE 'luke-refund:%'", peer.Id)
                is [[1L]], "卢克失败后两队八人分别获得一次原票邮件");
        first[0].Send(15, BitConverter.GetBytes(5054));
        That(Drain(first[0]).Single(packet => packet is { Kind: 1, Opcode: 15 }).Body[0] == 0, "本次失败攻坚不可重新选图，需离团重建");
    }

    // 另一分队离城是上一阶段的真实更新，不能混入候战队自己的选图断言。
    static List<WirePacket>[] ReadOtherPartyDeparture(TcpPeer[] departed, TcpPeer[] waiting)
    {
        var rows = waiting.Select(peer => Drain(peer)).ToArray();
        ushort[] departedActors = departed.Select(peer => peer.Id).Order().ToArray();
        foreach (var updates in rows)
        {
            That(!updates.Any(packet => packet.Opcode is 27 or 28 or 29 or 1536),
                "另一分队离城更新不提前为候战队打开选图或安装Run");
            var leaves = updates.Where(packet => packet is { Kind: 0, Opcode: 6 }).ToArray();
            That(leaves.Length == 4 && leaves.All(packet => packet.Body.Length == 2) &&
                leaves.Select(packet => U16(packet.Body)).Distinct().Count() == 4,
                "候战队恰收首队四个不同Actor离城，不吞未知或重复离场");
            leaves = leaves.OrderBy(packet => U16(packet.Body)).ToArray();
            for (int index = 0; index < departedActors.Length; index++)
                Bytes(leaves[index].Body, Build(writer => writer.Write(departedActors[index])),
                    "另一队离场逐字对应首队真实Actor，本队Actor及P2P保留");
        }
        return rows;
    }
}
