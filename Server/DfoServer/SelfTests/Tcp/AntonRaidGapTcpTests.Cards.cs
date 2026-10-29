using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidGapTcpTests
{
    // 真实Runtime与PVF冻结策略；不安装合成601或把Luke16B结果当作Anton展示。
    // 读宽来源：01DC8B70、023B2A54..2AAA、01101417..01446；见副本协议翻牌章节。
    public static async Task RunCard600601Async(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new LukeRaidClockTests.Clock(DateTimeOffset.Parse("2026-10-28T00:00:00Z"));
        await using var fixture = await BootAsync(source, root, resources, "anton-raid-card600-601",
            """{"StartMinimumOverride":1,"AntonFormalEntryEnabled":true,"AntonPhaseProgressEnabled":true}""", clock);
        Create(fixture, resources, "antoncards", "AntonCards", 2);
        using var peer = Open(fixture, "antoncards", gamePort: ChannelTcpTests.Endpoints(fixture)[19], channelNumber: 19);
        peer.Select(); AntonRaidTcpTests.Drain(peer);
        Denied([1, 0], "没有Battle不能以660生成奖励");

        var report = AntonTcpProbe.Play(peer, Catalog(resources), "reward");
        That(report.Reward && report.Opcode601 == 0,
            "UNIMPLEMENTED：真实Reward不补601业务列，重复117不生成600完整展示");
        // 不重新抽奖；直接读资格事务已经冻结的原计划，随机结果以数据库为准。
        string frozen = fixture.Snapshot("SELECT phase0_event,phase0_receipt FROM character_raid_runs");
        string waiting = State();
        int callbacks = clock.Callbacks;
        // 未发任何660时跨过休息600和阶段总限2400，不能由旧时钟代替领取。
        clock.Advance(3000);
        var idle = AntonTcpProbe.Barrier(peer, "Reward无领取跨过休息及总限");
        That(idle.All(p => p.Opcode is not (587 or 596 or 597 or 600 or 601)) &&
            waiting == State() && clock.Callbacks == callbacks &&
            fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[0L]],
            "Reward未领取不发奖、不自动推进、不执行旧阶段时钟；601展示仍UNIMPLEMENTED");
        foreach (byte[] body in new byte[][] { [], [1], [1, 0, 0], [2, 0], [1, 1], [0, 255] })
            Denied(body, "畸形长度或未配置kind/card不伪造展示和成功");

        var first = Claim(1, advances: false);
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[1L]], "首牌提交真实资产回执");
        That(first.FindIndex(p => p.Opcode == 14) >= 0 &&
            first.FindIndex(p => p.Opcode == 14) < first.FindIndex(p => p.Opcode == 600),
            "库存真实UID投影先于空600展示和ACK660");
        string assets = State();
        Claim(1, advances: false);
        That(assets == State(), "同阶段同牌只回放原回执，不增发或重抽");
        Denied([1, 1], "已领后换牌不能制造另一笔奖励");
        Claim(0, advances: true);
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[2L]] &&
            frozen == fixture.Snapshot("SELECT phase0_event,phase0_receipt FROM character_raid_runs"),
            "两类奖励提交后保留原阶段资格和冻结计划");
        assets = State();
        foreach (byte kind in new byte[] { 1, 0 })
        {
            peer.Send(660, [kind, 0]);
            var replay = AntonTcpProbe.Barrier(peer, "阶段1重放阶段0回执");
            That(replay.Count(p => p is { Kind: 1, Opcode: 660, Body: [1] }) == 1 &&
                replay.All(p => p.Opcode is not (600 or 601)) && assets == State(),
                "旧阶段660可确认原资产，不能把600/601装入新阶段窗口");
        }
        Console.WriteLine("UNIMPLEMENTED anton-card600-601: 601 C origin/D meaning/E formula/F meaning; 600 owner/code/flag and complete display allocation");

        string State() => fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2") +
            fixture.Snapshot("SELECT * FROM character_raid_reward_receipts ORDER BY phase,kind") +
            fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY 1") +
            fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id");

        void Denied(byte[] body, string why)
        {
            string before = State();
            peer.Send(660, body);
            var packets = AntonTcpProbe.Barrier(peer, why);
            That(packets.All(p => p.Opcode is not (600 or 601 or 660)) && before == State(), why);
        }

        List<WirePacket> Claim(byte kind, bool advances)
        {
            peer.Send(660, [kind, 0]);
            var packets = AntonTcpProbe.Barrier(peer, "正式660领奖kind" + kind);
            var display = packets.Single(p => p is { Kind: 0, Opcode: 600 });
            Bytes(display.Body, [kind, 0], "UNIMPLEMENTED：保留既有空600合同，不用资产数量补未知展示行");
            That(packets.Count(p => p is { Kind: 1, Opcode: 660, Body: [1] }) == 1 && packets.All(p => p.Opcode != 601) &&
                packets.IndexOf(display) < packets.FindIndex(p => p is { Kind: 1, Opcode: 660 }),
                "合法660资产提交后先600再ACK01，不补601业务列");
            That(packets.Any(p => p is { Kind: 0, Opcode: 587, Body: [2, 1] }) == advances,
                "全部规定牌种确认后才发送阶段1的587");
            return packets;
        }
    }
}
