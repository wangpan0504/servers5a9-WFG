using System.Buffers.Binary;
using DfoServer.Game.Raids;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidGapTcpTests
{
    // 真实Runtime、PVF、随机监听和39/45/117/660；不手工安装奖励或改Battle推进。
    public static async Task RunPhaseOneAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new LukeRaidClockTests.Clock(DateTimeOffset.Parse("2026-10-28T00:00:00Z"));
        await using var fixture = await BootAsync(source, root, resources, "anton-raid-phase1",
            """{"StartMinimumOverride":1,"AntonFormalEntryEnabled":true,"AntonPhaseProgressEnabled":true}""", clock);
        Create(fixture, resources, "phaseformal", "PhaseFormal", 2);
        var main = SelfTestCatalogs.Dungeons(resources.Archive);
        var raidResources = DfoServer.GameWorld.Raids.RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive, main,
            resources.ItemResources.Items, antonMapPath: DfoServer.GameWorld.Raids.AntonRaidMapPaths.Bind(main.MapPath),
            antonMapPoolPaths: DfoServer.GameWorld.Raids.AntonRaidMapPaths.Paths).Find(0)!.Resources!;
        var catalog = raidResources.EntryCatalog!;
        using var peer = Open(fixture, "phaseformal", gamePort: ChannelTcpTests.Endpoints(fixture)[19], channelNumber: 19);
        peer.Select(); AntonRaidTcpTests.Drain(peer);
        var report = AntonTcpProbe.Play(peer, catalog, "start");
        That(fixture.Query("SELECT raid_type,state FROM character_raid_runs") is [[0L,0L]], "正式666同扣票事务登记Anton生命周期");
        peer.Send(15, BitConverter.GetBytes(210u));
        That(AntonTcpProbe.Barrier(peer, "正式打开210").Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }), "正式开战能选210");
        AntonTcpProbe.Enter(peer, catalog, 210, report, clear: true);
        AssertState(report, 211, 1); AssertState(report, 216, 2);
        clock.Advance(19);
        AntonTcpProbe.RejectClosed(peer, null, 216);
        clock.Advance(1);
        State(AntonTcpProbe.Barrier(peer, "本服秒制reserve216到期"), 216, 0);
        // 先清212/214，避免其SET1在300秒同时触发脚本的黑雾重置，覆盖211开放。
        foreach (int dungeon in new[] { 212, 214 }) AntonTcpProbe.Enter(peer, catalog, dungeon, report, clear: true);
        AntonTcpProbe.Enter(peer, catalog, 216, report, clear: true);
        AssertState(report, 216, 1);
        clock.Advance(149); AntonTcpProbe.RejectClosed(peer, null, 216);
        clock.Advance(1); State(AntonTcpProbe.Barrier(peer, "216重开150秒"), 216, 0);
        clock.Advance(129); AntonTcpProbe.RejectClosed(peer, null, 211);
        clock.Advance(1); State(AntonTcpProbe.Barrier(peer, "211开放300秒"), 211, 0);
        foreach (int dungeon in new[] { 213, 215 })
            AntonTcpProbe.Enter(peer, catalog, dungeon, report, clear: true, leave: dungeon != 215);
        That(report.Reward, "真实阶段0CLEAR PHASE抵达Reward");
        That(fixture.Query("SELECT state,phase0_receipt IS NOT NULL,phase1_receipt IS NOT NULL FROM character_raid_runs") is [[0L,1L,0L]],
            "Reward前已落阶段0完整资格和奖励计划，不提前记最终通关");
        string phase0 = fixture.Snapshot("SELECT phase0_event,phase0_receipt FROM character_raid_runs");
        peer.Send(117, report.LastClearCheck!); AntonTcpProbe.Barrier(peer, "重复阶段0清图");
        That(phase0 == fixture.Snapshot("SELECT phase0_event,phase0_receipt FROM character_raid_runs"), "重复清图不重签阶段事件或重抽奖励");
        Claim(1, 0, false);
        string afterFirst = Assets();
        Claim(1, 0, false);
        That(afterFirst == Assets() && fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[1L]], "同牌回放不重复发放");
        Claim(0, 0, true);
        peer.Send(42); AntonTcpProbe.Barrier(peer, "奖励后回候战室");
        foreach (int dungeon in new[] { 220, 221, 222, 223, 224 }) AntonTcpProbe.RejectClosed(peer, null, dungeon);
        AntonTcpProbe.Enter(peer, catalog, 218, report, clear: true, leave: false);
        AssertState(report, 218, 0);
        int hpSymbol = raidResources.Script.Symbols["[218 dgn hp]"];
        var hpSnapshot = report.Symbols.Last();
        That(Enumerable.Range(0, hpSnapshot[0]).Any(index =>
            BinaryPrimitives.ReadInt32LittleEndian(hpSnapshot.AsSpan(1 + index * 8)) == hpSymbol &&
            BinaryPrimitives.ReadInt32LittleEndian(hpSnapshot.AsSpan(5 + index * 8)) == 0), "218 clear沿583同步PVF明示HP0");
        peer.Send(117, report.LastClearCheck!); AntonTcpProbe.Barrier(peer, "218重复clear不重计");
        peer.Send(42); AntonTcpProbe.Barrier(peer, "218回候战室");
        int volcanoMap = report.Maps.Count;
        AntonTcpProbe.Enter(peer, catalog, 219, report, clear: true);
        That(U32(report.Maps[volcanoMap], 14) == 19206 && report.Maps[volcanoMap][0] == 0 && report.Maps[volcanoMap][1] == 1,
            "219正式29使用原表19206起点，实际坐标取DGN 0,1");
        AssertState(report, 218, 2); AssertState(report, 219, 2); AssertState(report, 220, 0);
        AntonTcpProbe.RejectClosed(peer, null, 218);
        AntonTcpProbe.Enter(peer, catalog, 220, report, clear: true, leave: false);
        That(fixture.Query("SELECT state,phase1_receipt IS NOT NULL,clear_week IS NOT NULL FROM character_raid_runs") is [[2L,1L,1L]],
            "220 CLEAR PHASE在领取前持久记录最终通关且只占一次");
        string completed = fixture.Snapshot("SELECT * FROM character_raid_runs");
        peer.Send(117, report.LastClearCheck!); AntonTcpProbe.Barrier(peer, "重复最终clear");
        That(completed == fixture.Snapshot("SELECT * FROM character_raid_runs"), "重复最终通关不增加次数或改奖励");
        Claim(1, 1, false); Claim(0, 1, true);
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[4L]], "两阶段两独立牌种完整实际提交四笔");
        string finalAssets = Assets();
        Claim(0, 1, false);
        That(finalAssets == Assets() && completed == fixture.Snapshot("SELECT * FROM character_raid_runs"), "Completed同牌重放不改资产或通关次数");
        peer.Dispose(); await fixture.StopAsync();
        _ = new CharacterRepository(fixture.Database);
        That(finalAssets == Assets() && completed == fixture.Snapshot("SELECT * FROM character_raid_runs"), "重开仓储保留完整资产、阶段事件、冻结奖励和最终周通关");
        await fixture.StartAsync("reopened");
        using var reopened = Open(fixture, "phaseformal", gamePort: ChannelTcpTests.Endpoints(fixture)[19], channelNumber: 19);
        reopened.Select(); AntonRaidTcpTests.Drain(reopened);
        That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE raid_type=0 AND state=2") is [[1L]], "重建Host不重计最终通关");
        int callbacks = clock.Callbacks; clock.Advance(3600);
        That(clock.Callbacks == callbacks, "结束/Host停止后旧实例时钟已回收");

        string Assets() => fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2") + fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id");
        void Claim(byte kind, byte phase, bool advances)
        {
            peer.Send(660, [kind, 0]);
            var packets = AntonTcpProbe.Barrier(peer, "正式阶段" + phase + "领奖kind" + kind);
            That(packets.Any(p => p is { Kind: 1, Opcode: 660, Body: [1] }), "正式660在资产提交后成功");
            That(packets.All(p => p.Opcode != 601), "未猜601业务列");
            if (advances) That(packets.Any(p => p is { Kind: 0, Opcode: 587 } && p.Body.AsSpan().SequenceEqual(phase == 0 ? new byte[] {2,1} : new byte[] {4,1})),
                "两种奖励都确认后才推进下一阶段或Completed");
            else That(!packets.Any(p => p is { Kind: 0, Opcode: 587 } && p.Body is [2,1] or [4,1]), "单种或重复奖励不提前推进");
        }
    }

    static void State(IEnumerable<WirePacket> packets, int dungeon, byte state) => That(packets.Any(p =>
        p is {Kind:0,Opcode:586,Body.Length:6} && BinaryPrimitives.ReadInt32LittleEndian(p.Body) == dungeon && p.Body[4] == 0 && p.Body[5] == state),
        $"自然调度发布完整586：{dungeon}={state}");
}
