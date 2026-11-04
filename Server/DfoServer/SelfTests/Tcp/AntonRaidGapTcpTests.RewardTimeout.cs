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
            Create(fixture, resources, "timeoutcards", "TimeoutCards", 2, pcroom: partial);
            using var peer = Open(fixture, "timeoutcards", gamePort: ChannelTcpTests.Endpoints(fixture)[19], channelNumber: 19);
            peer.Select(premiumPcRoom: partial); AntonRaidTcpTests.Drain(peer);
            var report = AntonTcpProbe.Play(peer, Catalog(resources), "reward");
            That(report.Reward, "正式阶段一成功进入Reward");
            clock.Advance(25);
            That(AntonTcpProbe.Barrier(peer, "影片尚未结束").All(p => p.Opcode is not (600 or 24)), "原20秒截止不再提前发奖或回城");
            peer.Send(659, [1]); AntonTcpProbe.Barrier(peer, "影片完成后开始20秒选牌");
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
            var timeout = AntonTcpProbe.Barrier(peer, "20秒到期自动领取");
            // ACK先写出，原命令门禁可能尚未释放；推进可控时钟的有界退避，按真实587观察完成。
            for (int attempt = 0; attempt < 4 && !timeout.Any(p => p is { Opcode: 587, Body: [5,0] }); attempt++)
            {
                clock.Advance(1 << attempt);
                timeout.AddRange(AntonTcpProbe.Barrier(peer, "在线领取门禁退避"));
            }
            That(timeout.Count(p => p is { Opcode: 587, Body: [5,0] }) == 1 &&
                timeout.Any(p => p is { Opcode: 597, Body: [1,0x53,2,0,0] }),
                "无人或部分选牌均只推进一次至原595秒休息");
            That(assets != fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2") +
                fixture.Snapshot("SELECT * FROM character_raid_reward_receipts ORDER BY phase,kind") &&
                Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts")[0][0]) == (partial ? 3 : 2),
                "到期在线普通成员提交1/3，部分领取PC成员补齐2/3，均不增发");
            peer.Send(660, [0, 0]);
            That(AntonTcpProbe.Barrier(peer, "超时后回放").Any(p => p is { Kind: 1, Opcode: 660, Body: [1] }) &&
                Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts")[0][0]) == (partial ? 3 : 2),
                "休息态重放到期已提交回执，不增发");
            peer.Send(666);
            That(AntonTcpProbe.Barrier(peer, "休息后准备").Any(p => p is { Kind: 1, Opcode: 666, Body: [1] }),
                "超时兜底后团长仍可准备二阶段");
            clock.Advance(3); clock.FireQueued();
            That(AntonTcpProbe.Barrier(peer, "二阶段").Count(p => p is { Opcode: 587, Body: [2,1] }) == 1,
                "放弃旧阶段奖励后正常进入二阶段");
        }
    }
}
