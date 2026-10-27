using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class LukeRaidRunTcpTests
{
    internal static void PhaseTimeout(TcpTestFixture fixture, TcpPeer[] peers, LukeRaidClockTests.Clock clock)
    {
        var first = peers[..4]; var second = peers[4..];
        Enter(first, first[0], 5054); Load(first);
        Enter(second, second[0], 5055); Load(second);
        foreach (var peer in peers) Drain(peer);
        string assets = fixture.Snapshot("SELECT character_id,item_uid,item_core FROM character_inventory_items ORDER BY character_id,item_uid");
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
        That(fixture.Snapshot("SELECT character_id,item_uid,item_core FROM character_inventory_items ORDER BY character_id,item_uid") == assets &&
            fixture.Snapshot("SELECT id,revive_coins FROM characters ORDER BY id") == coins,
            "自动失败退场不再次扣票/扣币，也不伪造退款或奖励");
        first[0].Send(15, BitConverter.GetBytes(5054));
        That(Drain(first[0]).Single(packet => packet is { Kind: 1, Opcode: 15 }).Body[0] == 0, "本次失败攻坚不可重新选图，需离团重建");
    }
}
