using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidGapTcpTests
{
    // 真实Runtime/PVF/正式清图；不安装合成展示或伪造领取回执。
    public static async Task RunRewardTimeoutAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        foreach (bool partial in new[] { false, true })
        {
            var clock = new LukeRaidClockTests.Clock(DateTimeOffset.Parse("2026-10-28T00:00:00Z"));
            await using var fixture = await BootAsync(source, root, resources, "anton-reward-timeout-" + partial,
                """{"StartMinimumOverride":1,"AntonFormalEntryEnabled":true,"AntonPhaseProgressEnabled":true}""", clock);
            Create(fixture, resources, "timeoutcards", "TimeoutCards", 2);
            using var peer = Open(fixture, "timeoutcards", gamePort: ChannelTcpTests.Endpoints(fixture)[19], channelNumber: 19);
            peer.Select(); AntonRaidTcpTests.Drain(peer);
            var report = AntonTcpProbe.Play(peer, Catalog(resources), "reward");
            That(report.Reward, "正式阶段一成功进入Reward");
            if (partial)
            {
                peer.Send(660, [1, 0]);
                That(AntonTcpProbe.Barrier(peer, "部分领取").Any(p => p is { Kind: 1, Opcode: 660, Body: [1] }),
                    "期限内第一牌种提交资产");
            }
            peer.Send(42);
            AntonTcpProbe.Barrier(peer, "结算期回城");
            string assets = fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2") +
                fixture.Snapshot("SELECT * FROM character_raid_reward_receipts ORDER BY phase,kind");
            clock.Advance(19);
            That(AntonTcpProbe.Barrier(peer, "截止前不推进").All(p => p is not { Opcode: 587, Body: [5,0] }),
                "无人选牌或部分领取回城后仍保留20秒窗口");
            peer.Send(666);
            Bytes(AntonTcpProbe.Barrier(peer, "Reward中666").Single(p => p is { Kind: 1, Opcode: 666 }).Body,
                [0, 19, 0], "Reward中666明确失败");
            clock.Advance(1);
            var timeout = AntonTcpProbe.Barrier(peer, "20秒放弃未领项");
            That(timeout.Count(p => p is { Opcode: 587, Body: [5,0] }) == 1 &&
                timeout.Any(p => p is { Opcode: 597, Body: [1,0x53,2,0,0] }),
                "无人或部分选牌均只推进一次至原595秒休息");
            That(assets == fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2") +
                fixture.Snapshot("SELECT * FROM character_raid_reward_receipts ORDER BY phase,kind"),
                "放弃不生成资产或虚假领取回执");
            peer.Send(660, [0, 0]);
            That(AntonTcpProbe.Barrier(peer, "超时后未领项").All(p => p.Opcode is not (600 or 660)),
                "休息态不能补领已放弃的未领项");
            peer.Send(666);
            That(AntonTcpProbe.Barrier(peer, "休息后准备").Any(p => p is { Kind: 1, Opcode: 666, Body: [1] }),
                "超时兜底后团长仍可准备二阶段");
            clock.Advance(3); clock.FireQueued();
            That(AntonTcpProbe.Barrier(peer, "二阶段").Count(p => p is { Opcode: 587, Body: [2,1] }) == 1,
                "放弃旧阶段奖励后正常进入二阶段");
        }
    }
}
