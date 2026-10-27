using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class LukeRaidRunTcpTests
{
    // 同一个正常666/15/16 Runtime夹具内执行真实40/41，不手动绑定Battle或伪造扣币回执。
    static void ManualCoins(TcpTestFixture fixture, TcpPeer[] members, TcpPeer[] others, byte phase, int count)
    {
        var all = members.Concat(others).ToArray();
        for (int index = 0; index < count; index++)
        {
            var peer = members[index % members.Length];
            var ordered = new[] { peer }.Concat(all.Where(member => member != peer)).ToArray();
            // 先等发起者屏障，确保其完整Handler广播已入队，再读取其它连接。
            byte[] key = [(byte)peer.Id, (byte)(peer.Id >> 8)];
            long coins = Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", peer.Id)[0][0]);
            peer.Send(40, [0x74, 0, 4, 1, 0, 0]);
            var died = ordered.SelectMany(member => Drain(member, phaseResults: true)).ToArray();
            That(died.Count(row => row is { Kind: 0, Opcode: 32 } && U16(row.Body) == peer.Id && row.Body[2] == 0) == members.Length,
                "原Run死亡只向本分队投影，不伪造全团死亡");
            peer.Send(41, key);
            var frames = ordered.Select(member => (Peer: member, Rows: Drain(member, phaseResults: true))).ToArray();
            var own = frames.Single(row => row.Peer == peer).Rows;
            That(own.Count(row => row is { Kind: 1, Opcode: 41 } && row.Body.SequenceEqual(new byte[] { 1, key[0], key[1], 0 })) == 1 &&
                own.Any(row => row is { Kind: 0, Opcode: 14 }) &&
                Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", peer.Id)[0][0]) == coins - 1,
                "正常41扣本人原钱包一币并发原ACK/绝对余额14");
            That(frames.All(row => row.Rows.All(packet => packet.Opcode != 587 && packet.Opcode != 659)),
                "每次复活不重播587、不借有副作用659刷新计数");
            if (phase == 0)
                That(frames.All(row => row.Rows.All(packet => packet.Opcode != 778)), "P1权威池不发无效phase计数778");
            else
                That(frames.All(row => row.Rows.Count(packet => packet is { Kind: 0, Opcode: 778 } &&
                    packet.Body.SequenceEqual(new byte[] { checked((byte)(index + 1)) })) == 1),
                    "P2每个已提交扣币向全八名成员同步已用数，包括另一分队Town成员");
            peer.Send(41, key);
            var duplicate = ordered.SelectMany(member => Drain(member, phaseResults: true)).ToArray();
            That(!duplicate.Any(row => row.Opcode is 41 or 778) &&
                Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", peer.Id)[0][0]) == coins - 1,
                "重复41不再次扣本人币或推进共享计数");
        }
        if (phase == 0 && count == 8)
        {
            var peer = members[0]; byte[] key = [(byte)peer.Id, (byte)(peer.Id >> 8)];
            long coins = Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", peer.Id)[0][0]);
            peer.Send(40, [0x74, 0, 4, 1, 0, 0]);
            foreach (var member in all) Drain(member, phaseResults: true);
            peer.Send(41, key);
            var rejected = all.SelectMany(member => Drain(member, phaseResults: true)).ToArray();
            That(rejected.Count(row => row is { Kind: 1, Opcode: 41 } && row.Body.SequenceEqual(new byte[] { 0, 1, 0 })) == 1 &&
                !rejected.Any(row => row.Opcode is 14 or 778 || row is { Kind: 0, Opcode: 32 } && row.Body[2] == 1) &&
                Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", peer.Id)[0][0]) == coins,
                "P1四名队员共享8次，第9次即使本人有币仍拒绝且不扣资产");
            // 保持真实死亡，不人工复活；调用方随后走原42回城及同队再次入图。
        }
    }
}
