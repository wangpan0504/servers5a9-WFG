using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidGapTcpTests
{
    public static async Task RunPhaseTimersAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new LukeRaidClockTests.Clock(DateTimeOffset.Parse("2026-10-28T00:00:00Z"));
        await using var fixture = await BootAsync(source, root, resources, "anton-raid-phase-timer-221-224",
            """{"StartMinimumOverride":1,"AntonFormalEntryEnabled":true,"AntonPhaseProgressEnabled":true}""", clock);
        AntonRaidTimerTests.Run(fixture.Database, resources);
        var catalog = Catalog(resources);
        for (int phase = 0; phase < 2; phase++)
        {
            string account = "timertcp" + phase;
            Create(fixture, resources, account, "TimerTcp" + phase, 2);
            using var peer = Open(fixture, account, gamePort: ChannelTcpTests.Endpoints(fixture)[19], channelNumber: 19);
            peer.Select(); AntonRaidTcpTests.Drain(peer);
            var report = AntonTcpProbe.Play(peer, catalog, phase == 0 ? "start" : "card");
            if (phase == 0)
            {
                peer.Send(15, BitConverter.GetBytes(210u)); AntonTcpProbe.Barrier(peer, "超时测试选210");
                AntonTcpProbe.Enter(peer, catalog, 210, report, clear: false, leave: false);
            }
            else AntonTcpProbe.Enter(peer, catalog, 218, report, clear: false, leave: false);
            peer.Send(37, new byte[8]); AntonTcpProbe.Barrier(peer, "超时测试加载完成");
            string assets = fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2");
            string receipts = fixture.Snapshot("SELECT * FROM character_raid_reward_receipts ORDER BY 1,2");
            clock.Advance(2399);
            var before = AntonTcpProbe.Barrier(peer, "总deadline前一秒");
            That(before.All(packet => packet.Opcode is not (24 or 596 or 597)), "2400前不退场、不猜timer wire");
            clock.Advance(1);
            var frames = new List<WirePacket>();
            for (int count = 0; count < 256; count++)
            {
                // Read已校验帧长、CRC和连接序号；下面核对完整退场通知与禁止的未知wire。
                var packet = peer.Read(); frames.Add(packet);
                if (packet is { Kind: 0, Opcode: 24 }) break;
            }
            That(frames.Any(packet => packet is { Kind: 0, Opcode: 24 }) && frames.All(packet => packet.Opcode is not (587 or 596 or 597 or 600 or 601)),
                "本服2400秒总超时自动沿已有退场回城，不猜失败587、计时或奖励包");
            AntonTcpProbe.Barrier(peer, "超时退场完成");
            string prefix = phase == 0 ? "phase0" : "phase1";
            That(fixture.Query($"SELECT r.state,json_extract(r.{prefix}_receipt,'$.Eligible'),json_extract(r.{prefix}_receipt,'$.Reason'),r.clear_week FROM character_raid_runs r JOIN characters c ON c.id=r.character_id WHERE c.account=$p0", account)
                    is [[1L, 0L, "phase-timeout", DBNull]], "失败发布前持久state1和不合格阶段回执，无周通关");
            string failed = fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY 1,2");
            peer.Send(660, [1, 0]);
            var rejected = AntonTcpProbe.Barrier(peer, "超时后领奖拒绝");
            // P1无历史回执保持Unhandled；P2仍可幂等确认P1旧回执，但不能给失败阶段发奖或重开窗口。
            // 合同复用card600-601的旧阶段回放；不借Luke失败码，不撤销已提交的原资产确认。
            That(rejected.All(packet => packet.Opcode is not (600 or 601)) &&
                (phase == 0 ? rejected.All(packet => packet.Opcode != 660) :
                    rejected.Count(packet => packet is { Kind: 1, Opcode: 660, Body: [1] }) == 1),
                "失败阶段不发奖；无历史回执不猜失败ACK，已领旧阶段仅幂等确认");
            peer.Send(660, [1, 1]);
            That(AntonTcpProbe.Barrier(peer, "超时后未配置牌位拒绝").All(packet => packet.Opcode is not (660 or 600 or 601)),
                "失败后未配置牌位不能借历史回执生成成功或展示");
            AntonTcpProbe.RejectClosed(peer, null, phase == 0 ? 210 : 218);
            clock.Advance(600);
            var rest = AntonTcpProbe.Barrier(peer, "UNIMPLEMENTED休息边界");
            That(rest.All(packet => packet.Opcode is not (587 or 596 or 597)) && failed == fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY 1,2"),
                "UNIMPLEMENTED：600声明不擅自重开失败阶段、自动换phase或签发新事件");
            That(assets == fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2") &&
                receipts == fixture.Snapshot("SELECT * FROM character_raid_reward_receipts ORDER BY 1,2"), "超时与重复请求不扣票、退款或发奖");
        }
        await fixture.StopAsync();
        string saved = fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY 1,2");
        _ = new CharacterRepository(fixture.Database);
        That(saved == fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY 1,2"), "重开仓储保留两阶段失败结果，不伪称重建活跃战况");
        int callbacks = clock.Callbacks; clock.Advance(3600);
        That(clock.Callbacks == callbacks, "Host收尾后无旧计时回调");
    }
}
