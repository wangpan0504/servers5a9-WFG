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
        // 583消费者0237F210原样SET；按真实PVF逐图核对，避免把已清次数当成剩余HP。
        foreach (var dungeon in raidResources.Script.Phases[0].Dungeons)
            AssertHp(dungeon.Id, dungeon.HpRaw);
        That(fixture.Query("SELECT raid_type,state FROM character_raid_runs") is [[0L,0L]], "正式666同扣票事务登记Anton生命周期");
        peer.Send(15, BitConverter.GetBytes(210u));
        That(AntonTcpProbe.Barrier(peer, "正式打开210").Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }), "正式开战能选210");
        // 图内战况表：未清图时「回到城镇」72 01 02 走分队退场；「进入地下城」72 01 01 仍UNIMPLEMENTED，不回伪成功。
        AntonTcpProbe.Enter(peer, catalog, 210, report, clear: false);
        peer.Send(37, new byte[8]); AntonTcpProbe.Barrier(peer, "图内战况表加载210");
        peer.Send(72, [1, 1]);
        That(AntonTcpProbe.Barrier(peer, "图内战况表进入地下城未证").All(p => p.Opcode != 72), "图内72 01 01不伪造成功");
        peer.Send(72, [1, 2]);
        var boardReturn = AntonTcpProbe.Barrier(peer, "图内战况表回到城镇");
        int boardAck = boardReturn.FindIndex(p => p is { Kind: 1, Opcode: 72 });
        That(boardAck >= 0, "图内战况表回城必须有ACK72");
        Bytes(boardReturn[boardAck].Body, [1, 1, 2], "图内回城复用ACK72原state/option");
        That(boardReturn.FindIndex(p => p is { Kind: 0, Opcode: 24 }) > boardAck && boardReturn.All(p => p is not { Kind: 1, Opcode: 42 }),
            "图内战况表退场提交后回候战室，不混发ACK42");
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[0L]], "图内回城不产生阶段奖励");
        peer.Send(72, [1, 2]);
        That(AntonTcpProbe.Barrier(peer, "回城后重复图内回城").All(p => p.Opcode != 72), "回城后旧战况表请求不能重复退场");
        for (int clear = 0; clear < 4; clear++)
        {
            AntonTcpProbe.Enter(peer, catalog, 210, report, clear: true, leave: clear != 0);
            AssertHp(210, 3 - clear);
            if (clear != 0) continue;
            peer.Send(72, [1, 0]);
            That(AntonTcpProbe.Barrier(peer, "开放210不伪造重试失败").All(p => p.Opcode != 72),
                "开放图直接重试仍未闭合，不冒充准入失败或回城");
            peer.Send(72, [1, 1]);
            var singleReturn = AntonTcpProbe.Barrier(peer, "阶段仍战斗时单图结算选图");
            Bytes(singleReturn.Single(p => p is { Kind: 1, Opcode: 72 }).Body, [1, 1, 1], "86选图语义映射为原option1");
            int selectionAck = singleReturn.FindIndex(p => p is { Kind: 1, Opcode: 72 });
            That(singleReturn.FindIndex(p => p is { Kind: 1, Opcode: 15, Body: [1] }) > selectionAck &&
                singleReturn.FindIndex(p => p is { Kind: 0, Opcode: 27 } && p.Body.Length == 42) > selectionAck &&
                singleReturn.All(p => p.Opcode is not (24 or 28 or 29 or 660)),
                "已提交选图沿ACK72/ACK15/27建立场景，不回城、不自动建Run或领奖");
            peer.Send(72, [1, 1]);
            That(AntonTcpProbe.Barrier(peer, "选图后旧菜单").All(p => p.Opcode != 72), "旧菜单不能退出新选择");
            That(singleReturn.Any(p => p is { Kind: 0, Opcode: 27 }) &&
                fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[0L]],
                "单图退场回候战室且不替代660发阶段奖励");
        }
        peer.Send(117, report.LastClearCheck!);
        That(AntonTcpProbe.Barrier(peer, "HP归零后重复清图").All(packet => packet.Opcode is not (583 or 586)),
            "归零后旧117不再发布HP或副本状态变化");
        AssertHp(210, 0);
        AntonTcpProbe.RejectClosed(peer, null, 210);
        AssertState(report, 211, 1); AssertState(report, 216, 2);
        clock.Advance(19);
        AntonTcpProbe.RejectClosed(peer, null, 216);
        clock.Advance(1);
        State(AntonTcpProbe.Barrier(peer, "本服秒制reserve216到期"), 216, 0);
        // 先清212/214，避免其SET1在300秒同时触发脚本的黑雾重置，覆盖211开放。
        foreach (int dungeon in new[] { 212, 214 })
        {
            int mapsBefore = report.Maps.Count;
            AntonTcpProbe.Enter(peer, catalog, dungeon, report, clear: true);
            // PVF strongleg_high/high2：起点(1,1)、Boss(1,0)，两房只完成震颤单图。
            That(report.Maps.Count - mapsBefore == 2 && !report.Reward &&
                fixture.Query("SELECT phase0_receipt IS NOT NULL,phase1_receipt IS NOT NULL FROM character_raid_runs") is [[0L,0L]],
                "震颤两房不产生阶段资格或进入Reward");
        }
        AntonTcpProbe.Enter(peer, catalog, 216, report, clear: true);
        AssertState(report, 216, 1);
        clock.Advance(149); AntonTcpProbe.RejectClosed(peer, null, 216);
        clock.Advance(1); State(AntonTcpProbe.Barrier(peer, "216重开150秒"), 216, 0);
        clock.Advance(129); AntonTcpProbe.RejectClosed(peer, null, 211);
        clock.Advance(1); State(AntonTcpProbe.Barrier(peer, "211开放300秒"), 211, 0);
        foreach (int dungeon in new[] { 213, 215 })
            for (int clear = 0; clear < 2; clear++)
            {
                int mapsBefore = report.Maps.Count;
                AntonTcpProbe.Enter(peer, catalog, dungeon, report, clear: true, leave: dungeon != 215 || clear == 0);
                // PVF strongleg_lower/lower2：从(0,4)到(0,0)，五房全部保留，不能按文件名坐标截短。
                That(report.Maps.Count - mapsBefore == 5, "擎天每个Run沿DGN完整五房");
                AssertHp(dungeon, 1 - clear);
                if (dungeon == 215 && clear == 1) continue;
                That(!report.Reward && fixture.Query("SELECT phase0_receipt IS NOT NULL FROM character_raid_runs") is [[0L]],
                    "单腿或尚有HP的擎天不能提前封存阶段0");
            }
        That(report.Reward, "真实阶段0CLEAR PHASE抵达Reward");
        string menuBefore = Assets();
        peer.Send(72, [2, 1]);
        var focus = AntonTcpProbe.Barrier(peer, "安徒恩结算焦点0201");
        Bytes(focus.Single(p => p is { Kind: 1, Opcode: 72 }).Body, [1, 2, 1], "焦点只确认原状态，不退场或领牌");
        foreach (byte[] unsupported in new byte[][] { [1, 3], [1], [3, 2] })
        {
            peer.Send(72, unsupported);
            That(AntonTcpProbe.Barrier(peer, "未证团本菜单不伪造成功").All(p => p.Opcode != 72), "未知选项/畸形请求不回成功");
        }
        That(menuBefore == Assets(), "焦点及未实现选项不修改角色资产");
        That(fixture.Query("SELECT state,phase0_receipt IS NOT NULL,phase1_receipt IS NOT NULL FROM character_raid_runs") is [[0L,1L,0L]],
            "Reward前已落阶段0完整资格和奖励计划，不提前记最终通关");
        string phase0 = fixture.Snapshot("SELECT phase0_event,phase0_receipt FROM character_raid_runs");
        peer.Send(117, report.LastClearCheck!); AntonTcpProbe.Barrier(peer, "重复阶段0清图");
        That(phase0 == fixture.Snapshot("SELECT phase0_event,phase0_receipt FROM character_raid_runs"), "重复清图不重签阶段事件或重抽奖励");
        Claim(1, 0, false);
        string afterFirst = Assets();
        Claim(1, 0, false);
        That(afterFirst == Assets() && fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[1L]], "同牌回放不重复发放");
        clock.Advance(600);
        That(AntonTcpProbe.Barrier(peer, "UNIMPLEMENTED奖励期休息600").All(p => p.Opcode is not (587 or 596 or 597)) &&
            afterFirst == Assets(), "UNIMPLEMENTED：600不替代缺少的第二牌种，不自动推进Reward或发奖");
        // !220：回城不是领取；复用既有72退场，未完成kind仍须在候战室正式660提交。
        peer.Send(72, [1, 0]);
        var returning = AntonTcpProbe.Barrier(peer, "86重试准入失败后EPLP回候战室");
        int ack = returning.FindIndex(p => p is { Kind: 1, Opcode: 72 });
        That(ack >= 0, "团本结算返回必须有ACK72");
        Bytes(returning[ack].Body, [1, 1, 2], "已隐藏215不能重试，按86失败分支ACK72 option2");
        That(returning.FindIndex(p => p is { Kind: 0, Opcode: 3 }) > ack &&
            returning.FindIndex(p => p is { Kind: 0, Opcode: 24 }) > ack && returning.All(p => p is not { Kind: 1, Opcode: 42 }),
            "实际退场提交后ACK72先于本人状态及回城，不混发ACK42");
        string townAssets = Assets();
        peer.Send(72, [1, 2]);
        That(AntonTcpProbe.Barrier(peer, "回城后重复EPLP").All(p => p.Opcode != 72) && townAssets == Assets(),
            "旧菜单不能重复退场或修改新城镇状态");
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[1L]] &&
            returning.All(p => p is not { Kind: 0, Opcode: 587, Body: [2, 1] }),
            "未领完回城保持原Reward，不代领另一kind或推进阶段");
        AntonTcpProbe.RejectClosed(peer, null, 218);
        Claim(0, 0, true);
        foreach (var dungeon in raidResources.Script.Phases[1].Dungeons)
            AssertHp(dungeon.Id, dungeon.HpRaw);
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
        for (int clear = 0; clear < 5; clear++)
        {
            AntonTcpProbe.Enter(peer, catalog, 220, report, clear: true, leave: clear < 4);
            AssertHp(220, 4 - clear);
        }
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
        // 220最终Reward已通过660推进Completed，仍须沿72提交原Run退场，不能借完成状态重开。
        peer.Send(72, [1, 2]);
        var finalReturn = AntonTcpProbe.Barrier(peer, "全团Completed后返回攻坚候战室");
        int finalAck = finalReturn.FindIndex(p => p is { Kind: 1, Opcode: 72, Body: [1, 1, 2] });
        That(finalAck >= 0 && finalReturn.FindIndex(p => p is { Kind: 0, Opcode: 24 }) > finalAck &&
            finalReturn.All(p => p is not { Kind: 1, Opcode: 42 }), "最终回城提交后先ACK72再24");
        That(finalAssets == Assets() && completed == fixture.Snapshot("SELECT * FROM character_raid_runs"),
            "最终回城不重复通关、发奖或修改冻结资格");
        peer.Send(72, [1, 0]); peer.Send(72, [1, 2]);
        That(AntonTcpProbe.Barrier(peer, "Completed回城后的旧重试及回城").All(p => p.Opcode is not (72 or 28 or 29)),
            "旧菜单不能在Completed后安装新Run或重复成功");
        peer.Dispose(); await fixture.StopAsync();
        _ = new CharacterRepository(fixture.Database);
        That(finalAssets == Assets() && completed == fixture.Snapshot("SELECT * FROM character_raid_runs"), "重开仓储保留完整资产、阶段事件、冻结奖励和最终周通关");
        await fixture.StartAsync("reopened");
        using var reopened = Open(fixture, "phaseformal", gamePort: ChannelTcpTests.Endpoints(fixture)[19], channelNumber: 19);
        reopened.Select(); AntonRaidTcpTests.Drain(reopened);
        That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE raid_type=0 AND state=2") is [[1L]], "重建Host不重计最终通关");
        int callbacks = clock.Callbacks; clock.Advance(3600);
        That(clock.Callbacks == callbacks, "结束/Host停止后旧实例时钟已回收");

        void AssertHp(int id, int expected)
        {
            var definition = raidResources.Script.Phases.SelectMany(phase => phase.Dungeons).Single(row => row.Id == id);
            int symbol = raidResources.Script.Symbols[definition.HpSymbol];
            byte[] body = report.Symbols.Last();
            That(body.Length == 1 + body[0] * 8 && Enumerable.Range(0, body[0]).Count(index =>
                BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1 + index * 8)) == symbol &&
                BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(5 + index * 8)) == expected) == 1,
                $"583图{id}符号{symbol}剩余HP={expected}");
        }

        string Assets() => fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY 1,2") + fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id");
        void Claim(byte kind, byte phase, bool advances)
        {
            peer.Send(660, [kind, 0]);
            var packets = AntonTcpProbe.Barrier(peer, "正式阶段" + phase + "领奖kind" + kind);
            foreach (var packet in packets.Where(packet => packet is { Kind: 0, Opcode: 583 }))
                report.Symbols.Add(packet.Body.ToArray());
            That(packets.Any(p => p is { Kind: 1, Opcode: 660, Body: [1] }), "正式660在资产提交后成功");
            That(packets.All(p => p.Opcode != 601), "未猜601业务列");
            if (advances) That(packets.Any(p => p is { Kind: 0, Opcode: 587 } && p.Body.AsSpan().SequenceEqual(phase == 0 ? new byte[] {5,0} : new byte[] {4,1})),
                "两种奖励都确认后才进入休息或Completed");
            if (advances && phase == 0)
            {
                int stateAt = packets.FindIndex(p => p is { Opcode: 587, Body: [5,0] });
                int timerAt = packets.FindIndex(p => p.Opcode == 596);
                int remainAt = packets.FindIndex(p => p.Opcode == 597);
                That(stateAt >= 0 && timerAt > stateAt && remainAt > timerAt, "86休息广播顺序587→596→597");
                var timer = packets[timerAt].Body;
                That(timer.Length == 13 && timer[0] == 0 && BinaryPrimitives.ReadUInt32LittleEndian(timer.AsSpan(1)) == 0 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(timer.AsSpan(9)) == 595 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(timer.AsSpan(5)) > DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "596完整13B及真实Unix截止字段");
                Bytes(packets[remainAt].Body, [1, 0x53, 2, 0, 0], "REST600减5，槽1=595秒");
                clock.Advance(594);
                That(AntonTcpProbe.Barrier(peer, "595前保持休息").All(p => p.Opcode is not (594 or 587)), "休息不能提前开战");
                clock.Advance(1);
                var preparing = AntonTcpProbe.Barrier(peer, "自然准备P2");
                That(preparing.Any(p => p is { Opcode: 594, Body.Length: 0 }) && preparing.All(p => p is not { Opcode: 587, Body: [2,1] }), "到期先空594准备");
                clock.Advance(3);
                var started = AntonTcpProbe.Barrier(peer, "自然开始P2");
                That(started.Any(p => p is { Opcode: 587, Body: [2,1] }), "准备完成才进入第二阶段");
                foreach (var packet in started.Where(p => p.Opcode == 583)) report.Symbols.Add(packet.Body.ToArray());
            }
            else if (!advances) That(!packets.Any(p => p is { Kind: 0, Opcode: 587 } && p.Body is [2,1] or [4,1]), "单种或重复奖励不提前推进");
        }
    }

    static void State(IEnumerable<WirePacket> packets, int dungeon, byte state) => That(packets.Any(p =>
        p is {Kind:0,Opcode:586,Body.Length:6} && BinaryPrimitives.ReadInt32LittleEndian(p.Body) == dungeon && p.Body[4] == 0 && p.Body[5] == state),
        $"自然调度发布完整586：{dungeon}={state}");
}
