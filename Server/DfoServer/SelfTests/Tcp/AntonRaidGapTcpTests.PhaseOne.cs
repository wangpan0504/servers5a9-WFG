using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidGapTcpTests
{
    // 缺口测试：正式Runtime开战，不安装合成奖励、不手动改Battle或SQL推进阶段。
    // 依据：etc/raid/anton.etc两阶段及STATE REWARD原值；Docs/协议/副本.md的计时、600/660和668证据。
    public static async Task RunPhaseOneAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new LukeRaidClockTests.Clock(DateTimeOffset.Parse("2026-10-28T00:00:00Z"));
        await using var fixture = await BootAsync(source, root, resources, "anton-raid-phase1",
            """{"StartMinimumOverride":1,"AntonFormalEntryEnabled":true,"AntonPhaseProgressEnabled":true}""", clock);
        Create(fixture, resources, "phasegap", "PhaseGap", 2);
        var catalog = Catalog(resources);
        using var peer = Open(fixture, "phasegap", gamePort: ChannelTcpTests.Endpoints(fixture)[19], channelNumber: 19);
        peer.Select();
        AntonRaidTcpTests.Drain(peer);
        var report = AntonTcpProbe.Play(peer, catalog, "start");
        peer.Send(15, BitConverter.GetBytes(210u));
        That(AntonTcpProbe.Barrier(peer, "阶段缺口打开210").Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }), "正式开战能选210");
        AntonTcpProbe.Enter(peer, catalog, 210, report, clear: true);
        AssertState(report, 211, 1);
        AssertState(report, 216, 2);

        // 3600秒仅是合成观测窗口，绝不将资源20/300解释为秒。复用可控单调时钟，不等待真实一小时。
        clock.Advance(3600);
        NoProgress(AntonTcpProbe.Barrier(peer, "UNIMPLEMENTED 211/216自然计时"));
        foreach (int dungeon in new[] { 211, 216 }) AntonTcpProbe.RejectClosed(peer, null, dungeon);
        peer.Send(15, BitConverter.GetBytes(212u));
        That(AntonTcpProbe.Barrier(peer, "缺口对照打开已开放212").Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }),
            "同连接仍能打开有证已开放图，不能把会话失效当成计时拒绝");
        foreach (int dungeon in new[] { 211, 216 }) AntonTcpProbe.RejectSelect(peer, null, dungeon);
        foreach (int dungeon in new[] { 212, 214, 213, 215 })
            AntonTcpProbe.Enter(peer, catalog, dungeon, report, clear: true, leave: dungeon != 215);
        That(report.Reward, "正式阶段0清图到达Reward；未借领域夹具跨奖励门禁");

        string assets = fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2");
        // 只沿已有660两u8读宽及已观察0/1请求，不枚举或猜造其它牌种。
        foreach (byte[] card in new byte[][] { [1, 0], [0, 0], [1, 0], [0, 0] })
        {
            peer.Send(660, card);
            NoProgress(AntonTcpProbe.Barrier(peer, "UNIMPLEMENTED 正式660 " + Convert.ToHexString(card)));
        }
        peer.Send(117, report.LastClearCheck!);
        NoProgress(AntonTcpProbe.Barrier(peer, "Reward重复117"));
        clock.Advance(3600);
        NoProgress(AntonTcpProbe.Barrier(peer, "UNIMPLEMENTED Reward超时推进"));
        That(assets == fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
                fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2") &&
            fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[0L]],
            "UNIMPLEMENTED 正式奖励/翻牌：重复请求和合成时间推进不改资产、revision或回执");
        peer.Send(42);
        That(AntonTcpProbe.Barrier(peer, "Reward回候战室").Any(p => p is { Kind: 1, Opcode: 42, Body: [1] }),
            "Reward后仍能正式回城");
        // 42后再测15/16，避免仅因仍在Dungeon而拒绝，漏掉候战室中的阶段1授权。
        foreach (int dungeon in new[] { 218, 219, 220, 221, 222, 223, 224 })
            AntonTcpProbe.RejectClosed(peer, null, dungeon);
        peer.Send(15, new byte[4]);
        That(AntonTcpProbe.Barrier(peer, "Reward候战室选图").Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }),
            "Reward后通用选图仍可打开，下一阶段DGN授权保持拒绝");
        foreach (int dungeon in new[] { 218, 219, 220, 221, 222, 223, 224 })
            AntonTcpProbe.RejectSelect(peer, null, dungeon);
        await fixture.StopAsync();
        var events = fixture.Events();
        That(events.Count(e => e.GetProperty("Kind").GetString() == "anton_phase_progress" &&
                e.GetProperty("Detail").GetProperty("State").GetString() == "Reward") == 1 &&
            !events.Any(e => e.GetProperty("Kind").GetString() == "raid_reward_committed"),
            "只产生一次阶段0Reward，没有正式奖励提交");
        Console.WriteLine("UNIMPLEMENTED anton-raid-phase1: natural timers, resource rewards, Reward advance, card and stage1 TCP; rejection checks passed");

        static void NoProgress(IEnumerable<WirePacket> packets) => That(
            packets.All(p => p.Opcode is not (28 or 29 or 586 or 587 or 596 or 597 or 600 or 601 or 660)),
            "缺证路径不发布地图、阶段推进、计时或奖励；1278屏障确认同连接可继续");
    }
}
