using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidGapTcpTests
{
    // 真实Runtime与PVF冻结策略；不安装合成601或把Luke16B结果当作Anton展示。
    // 读宽来源：01DC8B70、023B2A54..2AAA、01101417..01446；见副本协议翻牌章节。
    public static async Task RunCard600601Async(ServerOptions source, string root, TcpTestResources resources)
    {
        AntonResourceRewardTests.Run(resources);
        var clock = new LukeRaidClockTests.Clock(DateTimeOffset.Parse("2026-10-28T00:00:00Z"));
        await using var fixture = await BootAsync(source, root, resources, "anton-raid-card600-601",
            """{"StartMinimumOverride":1,"AntonFormalEntryEnabled":true,"AntonPhaseProgressEnabled":true}""", clock);
        Create(fixture, resources, "antoncards", "AntonCards", 2, pcroom: true);
        using var peer = Open(fixture, "antoncards", gamePort: ChannelTcpTests.Endpoints(fixture)[19], channelNumber: 19);
        peer.Select(); AntonRaidTcpTests.Drain(peer);
        Denied([1, 0], "没有Battle不能以660生成奖励");

        var report = AntonTcpProbe.Play(peer, Catalog(resources), "reward");
        That(report.Reward && report.Opcode601 == 1,
            "真实Reward发布一次完整601，重复117不重开");
        clock.Advance(25);
        That(AntonTcpProbe.Barrier(peer, "资格提交25秒").All(p => p.Opcode is not (599 or 600 or 24)), "标题影片期间不发牌、不自动回城");
        Denied([1, 1], "影片期间660不授奖");
        peer.Send(659, [0]);
        var skip = AntonTcpProbe.Barrier(peer, "659请求跳过");
        That(skip.Any(p => p is { Kind: 1, Opcode: 659, Body: [1] }) && skip.Count(p => p.Opcode == 599) == 1 &&
            skip.All(p => p.Opcode != 600), "00只确认并提示跳过，不冒充影片结束");
        peer.Send(659, [1]);
        var opened = AntonTcpProbe.Barrier(peer, "659影片结束");
        int openedState = opened.FindIndex(p => p is { Opcode: 587, Body: [4,0] });
        int teamAt = opened.FindIndex(p => p is { Opcode: 600, Body: [3, ..] });
        That(opened.Any(p => p is { Kind: 1, Opcode: 659, Body: [1] }) && opened.Count(p => p.Opcode == 600) == 4 &&
            teamAt >= 0 && openedState > teamAt && opened.Where(p => p is { Opcode: 600, Body: [not 3, ..] })
                .All(packet => opened.IndexOf(packet) > openedState), "01后完整class2权益先于state4，随后才发布1/0/PC房2空选择列表");
        foreach (var packet in opened.Where(p => p is { Opcode: 600, Body: [not 3, ..] }))
            That(packet.Body.Length == 2 && packet.Body[1] == 0, "可选牌列表严格2B，不预填本人手动结果");
        var team = opened[teamAt].Body;
        That(team.Length >= 6 && team[1] == 1 && team[4] == 0 && team.Length == 6 + 16 * team[5] &&
            fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[0L]],
            "完整权益表为已持久阶段owner、冻结Card0与16B物品列，预填不发资产");
        peer.Send(659, [1]);
        That(AntonTcpProbe.Barrier(peer, "重复影片完成").All(p => p.Opcode is not (600 or 601 or 587)), "重复确认不重建牌组、不续期");
        // 不重新抽奖；直接读资格事务已经冻结的原计划，随机结果以数据库为准。
        string frozen = fixture.Snapshot("SELECT phase0_event,phase0_receipt FROM character_raid_runs");
        That(frozen.Contains("10304529") && frozen.Contains("10093974") && !frozen.Contains("10304474") &&
            !frozen.Contains("490703082"), "86单池算法按95现网PVF冻结实际材料，不把配置容器作为发奖物品");
        peer.Send(666);
        var deniedStart = AntonTcpProbe.Barrier(peer, "Reward中666明确拒绝");
        Bytes(deniedStart.Single(p => p is { Kind: 1, Opcode: 666 }).Body, [0, 19, 0],
            "Reward中666复用不可开始错误0x13，不能确认已开始");
        foreach (byte[] body in new byte[][] { [], [1], [1, 0, 0], [3, 0], [1, 4], [0, 255] })
            Denied(body, "畸形长度或未配置kind/card不伪造展示和成功");

        var first = Claim(1, advances: false);
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[1L]], "首牌提交真实资产回执");
        That(first.FindIndex(p => p.Opcode == 14) >= 0 &&
            first.FindIndex(p => p.Opcode == 14) < first.FindIndex(p => p is { Kind: 1, Opcode: 660 }),
            "库存真实UID投影先于ACK660");
        string assets = State();
        Claim(1, advances: false);
        That(assets == State(), "同阶段同牌只回放原回执，不增发或重抽");
        Claim(1, advances: false);
        That(assets == State(), "重复选择不制造另一笔奖励");
        Claim(0, advances: false);
        That(assets == State(), "自然kind0只确认牌背，不授予小队资产");
        Claim(2, advances: true);
        That(fixture.Query("SELECT kind FROM character_raid_reward_receipts ORDER BY kind") is [[1L], [2L], [3L]] &&
            frozen == fixture.Snapshot("SELECT phase0_event,phase0_receipt FROM character_raid_runs"),
            "团队1、PC房2及内部小队3提交后保留原阶段资格和冻结计划");
        clock.Advance(100);
        AntonTcpProbe.Barrier(peer, "休息100秒");
        peer.Send(666);
        var prepare = AntonTcpProbe.Barrier(peer, "团长空666提前准备");
        That(prepare.Any(p => p is { Kind: 1, Opcode: 666, Body: [1] }) &&
            prepare.Any(p => p is { Opcode: 594, Body.Length: 0 }) &&
            prepare.Any(p => p is { Opcode: 597, Body: [0,3,0,0,0] }), "86移植：团长提前开始且先准备3秒");
        clock.Advance(2);
        That(AntonTcpProbe.Barrier(peer, "准备未满").All(p => p is not { Opcode: 587, Body: [2,1] }), "准备2秒不得提前开始");
        clock.Advance(1); clock.FireQueued();
        That(AntonTcpProbe.Barrier(peer, "准备完成").Count(p => p is { Opcode: 587, Body: [2,1] }) == 1,
            "团长准备后只启动一次P2，旧休息回调无权重复推进");
        assets = State();
        foreach (byte kind in new byte[] { 1, 2 })
        {
            peer.Send(660, [kind, 3]);
            var replay = AntonTcpProbe.Barrier(peer, "阶段1重放阶段0回执");
            That(replay.Count(p => p is { Kind: 1, Opcode: 660, Body: [1] }) == 1 &&
                replay.All(p => p.Opcode is not (600 or 601)) && assets == State(),
                "旧阶段660可确认原资产，不能把600/601装入新阶段窗口");
        }
        Denied([0, 3], "新阶段不能回放旧阶段牌背或向新窗口发送600");
        clock.Advance(20);
        That(AntonTcpProbe.Barrier(peer, "旧20秒截止").All(p => p is not { Opcode: 587, Body: [5,0] }), "选牌推进后旧截止不能重复处理");

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
            peer.Send(660, [kind, 3]);
            var packets = AntonTcpProbe.Barrier(peer, "正式660领奖kind" + kind);
            That(packets.All(p => p.Opcode != 601) &&
                packets.Count(p => p is { Kind: 1, Opcode: 660, Body: [1] }) == 1,
                "正式资产提交后ACK01，660不重复发送阶段601");
            foreach (var packet in packets.Where(packet => packet.Opcode == 600))
                That(packet.Body is [_, 0] || packet.Body.Length >= 6 && packet.Body[1] == 1 &&
                    packet.Body.Length == 6 + 16 * packet.Body[5], "600仅使用空牌背或完整16B物品行");
            That(packets.Any(p => p is { Kind: 0, Opcode: 587, Body: [5, 0] }) == advances,
                "全部规定牌种确认后才发送休息态587");
            return packets;
        }
    }
}
