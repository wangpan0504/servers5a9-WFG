using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// 补开战失败回包、关闭阶段推进、以及同一分队两人的迁图/586。不改服务端规则。
internal static partial class AntonRaidGapTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await RunTicketsAsync(source, root, resources);
        await RunPhaseOffAsync(source, root, resources);
        await RunPartyAsync(source, root, resources);
    }

    static void AssertHpProjection(TcpPeer leader, TcpPeer? member = null)
    {
        uint context = 0x12345678;
        foreach (uint value in new uint[] { 100, 99, 0 })
        {
            // 两成员交替上报；两条百分比保留原顺序，第二次切换C以排除旧上下文值混用。
            var reporter = value == 99 ? member ?? leader : leader;
            if (value == 99) context++;
            reporter.Send(695, Build(w => { w.Write(context); w.Write((byte)2); w.Write(value); w.Write(99u); }));
            var expected = Build(w =>
            {
                w.Write((byte)1); w.Write((ushort)1); w.Write(member is null ? (byte)1 : (byte)2);
                w.Write(leader.Id); if (member is not null) w.Write(member.Id);
                w.Write(0u); w.Write(context); w.Write((byte)2); w.Write(value); w.Write(99u);
            });
            foreach (var peer in member is null ? new[] { leader } : new[] { leader, member })
            {
                var received = AntonTcpProbe.Barrier(peer, "695百分比投影" + value);
                Bytes(received.Single(p => p is { Kind: 0, Opcode: 634 }).Body, expected,
                    "634使用分队1、当前成员、已用币0、当前C与完整原序百分比");
                That(received.All(p => p.Opcode is not (115 or 586 or 601)), "HP0只更新展示，不提交Boss死亡或奖励");
            }
        }
        leader.Send(695, Build(w => { w.Write(context); w.Write((byte)1); w.Write(101u); }));
        That(AntonTcpProbe.Barrier(leader, "越界百分比不发布").All(p => p.Opcode != 634), "101不能污染634百分比");
        if (member is not null) That(AntonTcpProbe.Barrier(member, "越界队员屏障").All(p => p.Opcode != 634), "拒绝报告不向队员发布");
    }

    static async Task RunTicketsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = await BootAsync(source, root, resources, "anton-raid-gap-ticket",
            """{"StartMinimumOverride":1,"AntonFormalEntryEnabled":true,"AntonPhaseProgressEnabled":true}""");
        Create(fixture, resources, "gapbroke", "GapBroke", 0);
        Create(fixture, resources, "gaprich", "GapRich", 2);
        Create(fixture, resources, "gappoor", "GapPoor", 0);
        var port = ChannelTcpTests.Endpoints(fixture)[19];
        using var broke = Open(fixture, "gapbroke", gamePort: port, channelNumber: 19);
        using var rich = Open(fixture, "gaprich", gamePort: port, channelNumber: 19);
        using var poor = Open(fixture, "gappoor", gamePort: port, channelNumber: 19);
        broke.Select(); rich.Select(); poor.Select();
        AntonRaidTcpTests.Drain(broke); AntonRaidTcpTests.Drain(rich); AntonRaidTcpTests.Drain(poor);
        broke.Send(664, Build(w => { w.Write((byte)0); w.Write(Name("Broke")); w.Write(new byte[4]); }));
        AntonRaidTcpTests.Until(broke, 1, 664);
        AntonTcpProbe.Barrier(broke, "无票建队");
        broke.Send(669, Build(w => { w.Write(0u); w.Write((uint)broke.Id); w.Write(1u); }));
        Bytes(AntonRaidTcpTests.Until(broke, 1, 669).Body, [1], "无票角色仍能编队");
        broke.Send(666);
        Bytes(AntonRaidTcpTests.Until(broke, 1, 666).Body, [0, 19, 0], "一张票都没有时 666 回错误 19");
        That(fixture.Query("SELECT town, area FROM characters WHERE name='GapBroke'") is [[19L, 0L]], "无票开战留在频道落点 19/0，不进候战室 19/2");

        rich.Send(664, Build(w => { w.Write((byte)0); w.Write(Name("Rich")); w.Write(new byte[4]); }));
        AntonRaidTcpTests.Until(rich, 1, 664);
        poor.Send(10, AntonRaidTcpTests.Peer(rich.Id, 7, true));
        AntonRaidTcpTests.Until(poor, 1, 10);
        AntonRaidTcpTests.Until(rich, 0, 7);
        rich.Send(11, AntonRaidTcpTests.Peer(poor.Id, 7, false));
        AntonRaidTcpTests.Until(rich, 1, 11);
        AntonTcpProbe.Barrier(rich, "邀请排空");
        AntonTcpProbe.Barrier(poor, "邀请队员排空");
        rich.Send(666);
        Bytes(AntonRaidTcpTests.Until(rich, 1, 666).Body, [0, 19, 0], "未分队开战回错误 19");
        poor.Send(666);
        Bytes(AntonRaidTcpTests.Until(poor, 1, 666).Body, [0, 8, 0], "非队长开战回错误 8");
        rich.Send(669, Build(w => { w.Write(0u); w.Write((uint)rich.Id); w.Write(1u); }));
        Bytes(AntonRaidTcpTests.Until(rich, 1, 669).Body, [1], "队长编入第一分队");
        rich.Send(669, Build(w => { w.Write(0u); w.Write((uint)poor.Id); w.Write(1u); }));
        Bytes(AntonRaidTcpTests.Until(rich, 1, 669).Body, [1], "无票队员编入同一分队");
        rich.Send(666);
        Bytes(AntonRaidTcpTests.Until(rich, 1, 666).Body, [0, 19, 0], "队员票不足时整队 666 回错误 19");
        That(fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10096296") is [[2L]] &&
            fixture.Query("SELECT town, area FROM characters WHERE name='GapRich'") is [[19L, 0L]] &&
            fixture.Query("SELECT town, area FROM characters WHERE name='GapPoor'") is [[19L, 0L]],
            "票不足不扣队长的两张票，两人留在 19/0，不进候战室");
        await fixture.StopAsync();
        string?[] reasons = Reasons(fixture, "raid_start_request");
        bool accepted = Accepted(fixture);
        That(reasons.Count(reason => reason == "entry-cost-insufficient") == 2 && reasons.Contains("squads-unassigned") &&
            reasons.Contains("not-leader") && !accepted,
            "失败原因分别是无票、未分队和非队长，没有一次开战成功。实际 " + string.Join(',', reasons) + " accepted=" + accepted);
    }

    static async Task RunPhaseOffAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = await BootAsync(source, root, resources, "anton-raid-gap-phase-off",
            """{"StartMinimumOverride":1,"AntonFormalEntryEnabled":true}""");
        Create(fixture, resources, "gapoff", "GapOff", 2);
        var catalog = Catalog(resources);
        using var peer = Open(fixture, "gapoff", gamePort: ChannelTcpTests.Endpoints(fixture)[19], channelNumber: 19);
        peer.Select();
        AntonRaidTcpTests.Drain(peer);
        var report = AntonTcpProbe.Play(peer, catalog, "clear210");
        That(report.Entered210 && !report.Reward && report.Opcode586 == 0 && report.Opcode596 == 0 && report.Opcode597 == 0 && report.Opcode601 == 0,
            "关闭阶段推进时清 210 不发 586/596/597/601，也不进 Reward：" + report.Summary());
        That(fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10096296") is [[1L]],
            "关闭阶段推进仍按开战扣一张票");
        await fixture.StopAsync();
        var events = fixture.Events();
        That(events.Any(row => row.GetProperty("Kind").GetString() == "formal_dungeon_entry" &&
                row.GetProperty("Detail").GetProperty("Dungeon").GetInt32() == 210 &&
                row.GetProperty("Detail").GetProperty("Accepted").GetBoolean()) &&
            !events.Any(row => row.GetProperty("Kind").GetString() == "anton_phase_progress"),
            "关闭阶段推进仍能进 210，且没有 anton_phase_progress");
    }

    static async Task RunPartyAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = await BootAsync(source, root, resources, "anton-raid-gap-party",
            """{"StartMinimumOverride":1,"AntonFormalEntryEnabled":true,"AntonPhaseProgressEnabled":true}""");
        Create(fixture, resources, "gaplead", "GapLead", 2);
        Create(fixture, resources, "gapmate", "GapMate", 2);
        var catalog = Catalog(resources);
        var port = ChannelTcpTests.Endpoints(fixture)[19];
        using var leader = Open(fixture, "gaplead", gamePort: port, channelNumber: 19);
        using var member = Open(fixture, "gapmate", gamePort: port, channelNumber: 19);
        leader.Select(); member.Select();
        AntonRaidTcpTests.Drain(leader); AntonRaidTcpTests.Drain(member);
        leader.Send(664, Build(w => { w.Write((byte)0); w.Write(Name("Party")); w.Write(new byte[4]); }));
        AntonRaidTcpTests.Until(leader, 1, 664);
        member.Send(10, AntonRaidTcpTests.Peer(leader.Id, 7, true));
        AntonRaidTcpTests.Until(member, 1, 10);
        AntonRaidTcpTests.Until(leader, 0, 7);
        leader.Send(11, AntonRaidTcpTests.Peer(member.Id, 7, false));
        AntonRaidTcpTests.Until(leader, 1, 11);
        AntonTcpProbe.Barrier(leader, "双人邀请");
        AntonTcpProbe.Barrier(member, "双人邀请队员");
        leader.Send(669, Build(w => { w.Write(0u); w.Write((uint)leader.Id); w.Write(1u); }));
        Bytes(AntonRaidTcpTests.Until(leader, 1, 669).Body, [1], "队长进入第一分队");
        leader.Send(669, Build(w => { w.Write(0u); w.Write((uint)member.Id); w.Write(1u); }));
        Bytes(AntonRaidTcpTests.Until(leader, 1, 669).Body, [1], "队员进入同一分队");
        leader.Send(666);
        var started = AntonTcpProbe.Barrier(leader, "双人开战");
        var memberStarted = AntonTcpProbe.Barrier(member, "双人开战队员");
        That(started.Any(packet => packet is { Kind: 1, Opcode: 666, Body: [1] }) &&
            started.Any(packet => packet is { Kind: 0, Opcode: 23 } && packet.Body.Length >= 4 && packet.Body[2] == 19 && packet.Body[3] == 2) &&
            memberStarted.Any(packet => packet is { Kind: 0, Opcode: 23 } && packet.Body.Length >= 4 && U16(packet.Body) == member.Id &&
                packet.Body[2] == 19 && packet.Body[3] == 2),
            "两人开战成功并都落到 19/2");
        AntonTcpProbe.RejectClosed(leader, member, 216);
        AntonTcpProbe.RejectClosed(leader, member, 999);
        leader.Send(15, BitConverter.GetBytes(210u));
        var opened = AntonTcpProbe.Barrier(leader, "15 打开 210");
        AntonTcpProbe.Barrier(member, "15 打开 210 队员");
        That(opened.Any(packet => packet is { Kind: 1, Opcode: 15, Body: [1] }), "210 的 15 打开选图");
        AntonTcpProbe.RejectSelect(leader, member, 216);
        AntonTcpProbe.RejectSelect(leader, member, 210, 2);
        AntonTcpProbe.RejectSelect(leader, member, 210, 0x00090000);
        AntonTcpProbe.RejectSelect(leader, member, 999);
        var leaderReport = new AntonTcpProbe.AntonProbeReport();
        var memberReport = new AntonTcpProbe.AntonProbeReport();
        for (int clear = 0; clear < 4; clear++)
        {
            AntonTcpProbe.EnterSquad(leader, member, catalog, 210, leaderReport, memberReport, leave: clear != 0,
                loaded: clear == 0 ? () => AssertHpProjection(leader, member) : null);
            if (clear != 0) continue;
            member.Send(72, [1, 2]); AntonTcpProbe.Barrier(member, "一人先退出原Run");
            AntonTcpProbe.Barrier(leader, "同步先退出成员");
            leader.Send(72, [1, 1]);
            var incomplete = AntonTcpProbe.Barrier(leader, "86不完整原名单降为回城");
            That(incomplete.Any(p => p is { Kind: 1, Opcode: 72, Body: [1, 1, 2] }) &&
                incomplete.Any(p => p is { Kind: 0, Opcode: 24 }) && incomplete.All(p => p.Opcode != 27),
                "名单不全才允许0101降option2，原Run成员释放后可重新编队进入");
        }
        That(leaderReport.Maps.Count > 0 && memberReport.Maps.Count > 0, "两人都能收到 29");
        SameRoom(leaderReport.Maps[0], memberReport.Maps[0], 32200, "进 210 时两人 29 都在起点 32200");
        SameRoom(leaderReport.Maps[^1], memberReport.Maps[^1], null, "清到 Boss 房时两人 29 的地图和坐标一致");
        foreach (var (id, state) in new (int Id, byte State)[] { (210, 2), (211, 1), (212, 0), (213, 0), (214, 0), (215, 0), (216, 2) })
        {
            AssertState(leaderReport, id, state);
            AssertState(memberReport, id, state);
        }
        foreach (int dungeon in new[] { 212, 214, 213, 215 })
        {
            for (int clear = 0; clear < (dungeon is 213 or 215 ? 2 : 1); clear++)
                AntonTcpProbe.EnterSquad(leader, member, catalog, dungeon, leaderReport, memberReport, leave: dungeon != 215 || clear != 1);
            byte state = dungeon == 215 ? (byte)2 : dungeon == 213 ? (byte)3 : (byte)1;
            AssertState(leaderReport, dungeon, state);
            AssertState(memberReport, dungeon, state);
            SameRoom(leaderReport.Maps[^1], memberReport.Maps[^1], null, dungeon + " 清图迁移时两人地图键和坐标一致");
        }
        foreach (int dungeon in new[] { 210, 211, 212, 213, 214, 215, 216, 218, 219, 220, 221, 222, 223, 224 })
        {
            AssertState(leaderReport, dungeon, 2);
            AssertState(memberReport, dungeon, 2);
        }
        That(leaderReport.Reward && memberReport.Reward, "双腿成功时两人均收到 587 Reward/0");
        foreach (int dungeon in new[] { 218, 219, 220, 221, 222, 223, 224 })
            AntonTcpProbe.RejectClosed(leader, member, dungeon);
        That(leaderReport.Opcode596 > 0 && leaderReport.Opcode597 > 0 && leaderReport.Opcode601 == 1 &&
            memberReport.Opcode596 > 0 && memberReport.Opcode597 > 0 && memberReport.Opcode601 == 1,
            "双人阶段0均发布一次601");
        That(fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10096296") is [[2L]],
            "双人开战各扣一张，进图和清 210 不再扣");
        string frozen = fixture.Snapshot("SELECT phase0_event,phase0_receipt FROM character_raid_runs ORDER BY character_id");
        member.Send(72, [1, 1]);
        That(AntonTcpProbe.Barrier(member, "队员不能替全队选图").All(p => p.Opcode != 72), "86完整分队结算只接受队长操作");
        leader.Send(72, [1, 1]);
        var selectedLead = AntonTcpProbe.Barrier(leader, "全队结算选图队长");
        var selectedMate = AntonTcpProbe.Barrier(member, "全队结算选图队员");
        foreach (var selected in new[] { selectedLead, selectedMate })
            That(selected.Any(p => p is { Kind: 1, Opcode: 72, Body: [1, 1, 1] }) &&
                selected.Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }) &&
                selected.Any(p => p is { Kind: 0, Opcode: 27 } && p.Body.Length == 42) &&
                selected.All(p => p.Opcode is not (24 or 28 or 29 or 660)), "完整分队逐人确认选图并建立95场景，不伪造回城或新Run");
        That(frozen == fixture.Snapshot("SELECT phase0_event,phase0_receipt FROM character_raid_runs ORDER BY character_id") &&
            fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[0L]], "选图不代领、重抽或清除未领奖成员资格");
        leader.Send(72, [1, 0]);
        That(AntonTcpProbe.Barrier(leader, "选图后的旧菜单").All(p => p.Opcode != 72), "旧结果菜单不能退出新的选图状态");
        leader.Send(660, [1, 3]); member.Send(660, [1, 3]);
        var leadCards = AntonTcpProbe.Barrier(leader, "同队竞争第四张");
        var mateCards = AntonTcpProbe.Barrier(member, "同队竞争第四张队员");
        var picked = new[] { leadCards, mateCards }.Select(packets => packets.Single(p => p is { Kind: 0, Opcode: 600 }).Body[4]).Order().ToArray();
        That(picked.SequenceEqual(new byte[] { 0, 3 }) &&
            fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[2L]], "两成员同时选同牌只占不同位置，逐人提交原资产");
        leader.Send(660, [1, 3]); member.Send(660, [1, 3]);
        AntonTcpProbe.Barrier(leader, "队长重复点击"); AntonTcpProbe.Barrier(member, "队员重复点击");
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[2L]], "竞争后重复点击不重复发奖");
        leader.Send(660, [0, 3]); member.Send(660, [0, 3]);
        AntonTcpProbe.Barrier(leader, "竞争成员第二牌种"); AntonTcpProbe.Barrier(member, "竞争队员第二牌种");
        leader.Send(660, [1, 3]); member.Send(660, [1, 3]);
        var lateLead = AntonTcpProbe.Barrier(leader, "休息态重试原选位");
        var lateMate = AntonTcpProbe.Barrier(member, "休息态重试冲突原选位");
        That(new[] { lateLead, lateMate }.All(packets => packets.Any(p => p is { Kind: 1, Opcode: 660, Body: [1] })) &&
            fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[4L]],
            "冲突改位后跨休息态重发原请求仍确认同份回执，不增发");

    }

    static void SameRoom(byte[] leader, byte[] member, uint? map, string why) =>
        That(leader.Length >= 18 && member.Length >= 18 && leader[0] == member[0] && leader[1] == member[1] &&
            U32(leader, 14) == U32(member, 14) && (map is null || U32(leader, 14) == map), why);

    static void AssertState(AntonTcpProbe.AntonProbeReport report, int dungeon, byte state)
    {
        byte[]? body = report.States.LastOrDefault(row => row.Length >= 6 && BinaryPrimitives.ReadInt32LittleEndian(row) == dungeon);
        That(body is { Length: 6 } && body[4] == 0 && body[5] == state,
            $"586 应是 6 字节、保留字节 0、{dungeon} 状态 {state}，实际 " +
            (body is null ? "没有这张图" : Convert.ToHexString(body)));
    }

    static async Task<TcpTestFixture> BootAsync(ServerOptions source, string root, TcpTestResources resources, string name, string json,
        TimeProvider? clock = null)
    {
        var fixture = new TcpTestFixture(source, Path.Combine(root, name), resources: resources, clock: clock);
        Directory.CreateDirectory(fixture.Options.DataDirectory);
        File.WriteAllText(Path.Combine(fixture.Options.DataDirectory, "raid-start.json"), json);
        await fixture.StartAsync();
        return fixture;
    }

    static void Create(TcpTestFixture fixture, TcpTestResources resources, string account, string name, int tickets)
    {
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using var owner = owners.Acquire(account, () => { });
        var point = new Location(38, 0, 629, 196);
        var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(0), point, point, resources.Learning.Get);
        var learning = SkillPointLedger.Synchronize(0, 0, 85, candidate.InitialLearning with
            { Experience = resources.Growth.GetLevelStartExperience(85) }, resources.Learning);
        var created = repository.Create(owner, candidate with
            { Level = 85, AcceptedStoryLevel = 85, CompletedQuests = [12829, 3779], InitialLearning = learning });
        if (tickets == 0) return;
        var items = resources.ItemResources;
        InventoryService.Commit(repository, owner, created,
            new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(created, [new(10096296, tickets)]));
    }

    static RaidDungeonCatalog Catalog(TcpTestResources resources)
    {
        var main = SelfTestCatalogs.Dungeons(resources.Archive);
        return RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive, main, resources.ItemResources.Items,
            antonMapPath: AntonRaidMapPaths.Bind(main.MapPath), antonMapPoolPaths: AntonRaidMapPaths.Paths).Find(0)!.Resources!.EntryCatalog!;
    }

    static string?[] Reasons(TcpTestFixture fixture, string kind) => fixture.Events()
        .Where(row => row.GetProperty("Kind").GetString() == kind)
        .Select(row => row.GetProperty("Detail").GetProperty("Reason").GetString()).ToArray();

    static bool Accepted(TcpTestFixture fixture) => fixture.Events().Any(row => row.GetProperty("Kind").GetString() == "raid_start_request" &&
        row.GetProperty("Detail").GetProperty("Accepted").GetBoolean());
}
