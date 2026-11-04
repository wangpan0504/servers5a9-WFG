using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Raids;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class AntonRaidRejoinTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await RunCaseAsync(source, root, resources, enabled: false);
        await RunCaseAsync(source, root, resources, enabled: true);
    }

    public static Task RunRecoverAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunCaseAsync(source, root, resources, enabled: true, inRun: true);

    public static Task RunClientNotifyGapAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunCaseAsync(source, root, resources, enabled: true, inRun: true, clientNotifyGap: true);

    static async Task RunCaseAsync(ServerOptions source, string root, TcpTestResources resources, bool enabled,
        bool inRun = false, bool clientNotifyGap = false)
    {
        string name = clientNotifyGap ? "anton-raid-rejoin-client-notify" : inRun ? "anton-raid-rejoin-recover" :
            enabled ? "anton-raid-rejoin" : "anton-raid-rejoin-off";
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, name), resources: resources);
        Directory.CreateDirectory(fixture.Options.DataDirectory);
        string json = enabled
            ? """{"StartMinimumOverride":1,"AntonRejoinEnabled":true,"AntonFormalEntryEnabled":true,"AntonPhaseProgressEnabled":true}"""
            : """{"StartMinimumOverride":1}""";
        File.WriteAllText(Path.Combine(fixture.Options.DataDirectory, "raid-start.json"), json);
        await fixture.StartAsync();
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        for (int index = 0; index < 2; index++)
        {
            using var owner = owners.Acquire("antonrejoin" + index, () => { });
            var point = new Location(38, 0, 629, 196);
            var candidate = CharacterCreation.Prepare("Rej" + index, resources.Characters.Get(0), point, point, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 85, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(85) }, resources.Learning);
            var created = repository.Create(owner, candidate with
                { Level = 85, AcceptedStoryLevel = 85, CompletedQuests = [12829, 3779], InitialLearning = learning });
            var items = resources.ItemResources;
            InventoryService.Commit(repository, owner, created,
                new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(created, [new(10096296, 2)]));
        }
        using var leader = Open(fixture, "antonrejoin0", gamePort: endpoints[19], channelNumber: 19);
        using var member = Open(fixture, "antonrejoin1", gamePort: endpoints[19], channelNumber: 19);
        leader.Select();
        member.Select();
        AntonRaidTcpTests.Drain(leader);
        AntonRaidTcpTests.Drain(member);
        leader.Send(664, Build(w => { w.Write((byte)0); w.Write(Name("Rejoin")); w.Write(new byte[] { 0, 0, 0, 7 }); }));
        var createdList = AntonRaidTcpTests.Until(leader, 0, 590).Body;
        uint key = U32(createdList, 4);
        AntonRaidTcpTests.Until(leader, 0, 591);
        AntonRaidTcpTests.Until(leader, 1, 664);
        member.Send(10, AntonRaidTcpTests.Peer(leader.Id, 7, true));
        AntonRaidTcpTests.Until(member, 1, 10);
        var invite = AntonRaidTcpTests.Until(leader, 0, 7).Body;
        That(U16(invite) == member.Id && invite[2] == 10, "队长收到断线夹具的入队申请");
        leader.Send(11, AntonRaidTcpTests.Peer(member.Id, 7, false));
        AntonRaidTcpTests.Until(leader, 1, 11);
        AntonRaidTcpTests.Drain(leader);
        AntonRaidTcpTests.Drain(member);
        leader.Send(669, Build(w => { w.Write(0u); w.Write((uint)leader.Id); w.Write(1u); }));
        AntonRaidTcpTests.Until(leader, 1, 669);
        leader.Send(669, Build(w => { w.Write(0u); w.Write((uint)member.Id); w.Write(1u); }));
        AntonRaidTcpTests.Until(leader, 1, 669);
        AntonRaidTcpTests.Drain(leader);
        AntonRaidTcpTests.Drain(member);
        leader.Send(666);
        Bytes(ReadUntil(leader, 1, 666).Body, [1], "两人覆盖完成开战");
        QuietDrain(leader);
        QuietDrain(member);
        RaidDungeonCatalog? runCatalog = null;
        byte[]? runInfo = null, runMap = null;
        if (inRun)
        {
            var main = SelfTestCatalogs.Dungeons(resources.Archive);
            runCatalog = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive, main, resources.ItemResources.Items,
                antonMapPath: AntonRaidMapPaths.Bind(main.MapPath)).Find(0)!.Resources!.EntryCatalog!;
            leader.Send(15, BitConverter.GetBytes(210u));
            Bytes(AntonRaidTcpTests.Until(leader, 1, 15).Body, [1], "恢复测试沿正式Runtime选择210");
            QuietDrain(member);
            AntonTcpProbe.Enter(leader, runCatalog, 210, new(), clear: false, leave: false);
            var entry = AntonTcpProbe.Barrier(member, "断线前真实Run");
            That(entry.Any(p => p is { Kind: 0, Opcode: 28 }) && entry.Any(p => p is { Kind: 0, Opcode: 29 }),
                "两人已进入同一原Run，队员在加载确认前断线");
            runInfo = entry.Single(p => p is { Kind: 0, Opcode: 28 }).Body;
            runMap = entry.Single(p => p is { Kind: 0, Opcode: 29 }).Body;
            leader.Send(37, new byte[8]);
            That(AntonTcpProbe.Barrier(leader, "队长先确认加载").All(p => p is not { Kind: 0, Opcode: 30 }),
                "队员尚未确认时原共享加载屏障仍关闭");
            if (clientNotifyGap)
            {
                member.Send(37, new byte[8]);
                That(AntonTcpProbe.Barrier(member, "战斗中断线夹具实际加载").Any(p => p is { Kind: 0, Opcode: 30 }),
                    "纯呈现恢复夹具先以双方真实37进入业务Dungeon");
                AntonTcpProbe.Barrier(leader, "战斗中断线前同步队长");
                AntonTcpProbe.ClearRoom(leader, runMap, new(), out _, out _, false, member, new());
            }
        }
        ushort disconnected = member.Id;
        member.Dispose();
        bool left = false;
        for (int at = 0; at < 64 && !left; at++)
        {
            var packet = leader.Read();
            left = packet is { Kind: 0, Opcode: 591 } && U32(packet.Body, 4) == 3;
        }
        QuietDrain(leader);
        That(left && AntonRaidTcpTests.Information(leader, key, 1).Members.All(row => row.Character != disconnected),
            "断线后幸存者名册不再包含该成员");
        using var initialAgain = Open(fixture, "antonrejoin1", gamePort: endpoints[19], channelNumber: 19);
        var again = initialAgain;
        again.Select();
        string logs = fixture.Options.LogDirectory;
        if (!enabled)
        {
            AntonRaidTcpTests.Drain(again);
            string rejectedState = RejoinState();
            again.Send(668, Build(w => w.Write(key)));
            Bytes(AntonRaidTcpTests.Until(again, 1, 668).Body, [0, 19, 0], "关闭时668仍是原拒绝");
            AssertRejoinClean(fixture, again, "关闭");
            That(RejoinState() == rejectedState,
                "关闭重入及未授权选图不改变完整角色、库存、审计、奖励回执、团本Run或副本尝试");
            await fixture.StopAsync();
            var events = CompleteEvents(logs);
            That(!events.Any(row => row.GetProperty("Kind").GetString() == "raid_rejoin_notice"), "开关关闭不在重登后下发835");
            That(LastReason(events, "raid_start_request") == "formal-raid-rejoin-unavailable", "关闭时日志保持原拒绝原因");
            return;
        }
        var notice = ReadNotice(again);
        That(notice.Length == 7 && U32(notice) == key && notice[4] == 0 && notice[5] == 0 && notice[6] == 0,
            "重登后835携带原团key和未知零尾");
        string assets = RejoinAssets();
        var replay = Rejoin(key, "首次668原状态恢复", accepted: true);
        Rejoin(key, "重复668", accepted: false);
        Rejoin(key + 9, "错误key", accepted: false);
        var survivorRestored = AntonTcpProbe.Barrier(leader, "恢复场景不替幸存者加载");
        That(survivorRestored.All(p => p is not { Kind: 0, Opcode: 27 or 28 or 29 or 30 }),
            "668只向重返客户端重放场景，幸存者不收到重复27至30");
        if (clientNotifyGap)
        {
            // 在新场景尚未37时再次断线，不能把呈现屏障误记为业务Loading或遗失原Run能力。
            again.Dispose();
            AwaitMemberLeft(leader, "重入呈现中二次断线");
        }
        using var resumedAgain = clientNotifyGap ? Open(fixture, "antonrejoin1", gamePort: endpoints[19], channelNumber: 19) : null;
        if (resumedAgain is not null)
        {
            again = resumedAgain;
            again.Select();
            That(U32(ReadNotice(again)) == key, "呈现中二次断线仍捕获同一原团候选");
            replay = Rejoin(key, "呈现中二次断线恢复", accepted: true);
            // 恢复者的1278不能排空另一连接；幸存者先收齐正式名册，再发1361读取团表。
            var survivorRestoredAgain = AntonTcpProbe.Barrier(leader, "呈现中二次恢复同步幸存者名册");
            That(survivorRestoredAgain.All(p => p is not { Kind: 0, Opcode: 27 or 28 or 29 or 30 }) &&
                survivorRestoredAgain.Single(p => p is { Kind: 0, Opcode: 26 }).Body is [0],
                "二次恢复幸存者只同步成员与原主机槽0，不重复场景或加载");
            var roster = survivorRestoredAgain.Single(p => p is { Kind: 0, Opcode: 9 }).Body;
            That(roster.Length == 38 && U16(roster) == 1 && U16(roster, 2) == 9999 &&
                roster[9] == 2 && roster[11] == 2 && roster[12] == 0 && U16(roster, 13) == leader.Id &&
                roster[20] == 1 && U16(roster, 21) == again.Id && roster[28] == 0 && roster[30] == 0,
                "二次恢复使用action2完整两人名册，队长与新owner保留原普通分队槽及主机");
            Bytes(roster, survivorRestored.Single(p => p is { Kind: 0, Opcode: 9 }).Body,
                "二次恢复保持首次恢复的完整普通分队身份和成员投影");
        }
        That(RejoinAssets() == assets, "668首次、重复与错误key均不改变库存、资产审计或奖励回执");
        var members = AntonRaidTcpTests.Information(leader, key, 1).Members;
        That(members.Any(row => row.Character == again.Id) && members.Length == 2, "状态恢复使新owner回到原团名册，不建立新团");
        That(fixture.Query("SELECT town,area FROM characters WHERE name='Rej1'") is [[19L, 2L]],
            "状态恢复持久保存原候战室位置，不停在重登频道落点19/0");
        That(fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10096296") is [[2L]],
            "状态恢复和重复668没有再次扣票或退票");
        if (inRun)
        {
            byte[] replayInfo = replay.Single(p => p is { Kind: 0, Opcode: 28 }).Body;
            byte[] replayMap = replay.Single(p => p is { Kind: 0, Opcode: 29 }).Body;
            Bytes(replayInfo, runInfo!, "668重放原冻结地图信息，不重新抽取入口");
            That(replayMap[0] == runMap![0] && replayMap[1] == runMap[1] && U32(replayMap, 3) == U32(runMap, 3),
                "668使用原房间坐标和seed");
            if (clientNotifyGap) That(replayMap[18] == 0, "重连29只投影当前存活怪，不复活已清原房间");
            else Bytes(replayMap, runMap, "原业务加载中断线重放同一完整演员计划");
            again.Send(662, [110, 0, 0, 0, 100, 0, 0, 0, 2]);
            again.Send(660, [1, 0]);
            if (runMap![18] > 0)
                again.Send(39, FormalDungeonTcpTests.Death(U16(runMap, 25), again.Id, runMap[31]));
            again.Send(40, [0, 0, 0, 0]);
            var beforeLoaded = AntonTcpProbe.Barrier(again, "重入确认前禁止业务推进");
            That(beforeLoaded.All(p => p is not { Kind: 0, Opcode: 29 or 30 or 600 } && p is not { Kind: 1, Opcode: 660, Body: [1] }) &&
                RejoinAssets() == assets, "实际37前不释放加载或发放奖励资产");
            again.Send(37, new byte[8]);
            var loaded = AntonTcpProbe.Barrier(again, "收到重放29后确认原场景");
            That(loaded.Count(p => p is { Kind: 0, Opcode: 30 }) == 1 &&
                loaded.Any(p => p is { Kind: 1, Opcode: 37, Body: [1] }),
                "模拟客户端仅在收到完整场景后37，服务端才一次确认并释放本人30");
            That(AntonTcpProbe.Barrier(leader, "重入真实37后幸存者屏障").All(p => p is not { Kind: 0, Opcode: 30 }),
                "已加载幸存者不被要求重发37，也不重收30");
            again.Send(37, new byte[8]);
            That(AntonTcpProbe.Barrier(again, "重复重入37").All(p => p is not { Kind: 0, Opcode: 30 }),
                "同一重入呈现屏障只能消费一次");
            That(RejoinAssets() == assets, "原场景加载确认不重扣票、库存或重复奖励");
            RejectChangedRun(fixture, leader, again, key, runCatalog!, runInfo!, replayMap, firstRoomCleared: clientNotifyGap);
        }
        else
        {
            leader.Send(15, BitConverter.GetBytes(210u));
            Bytes(AntonRaidTcpTests.Until(leader, 1, 15).Body, [1], "恢复普通分队后原队长可重新选择原Battle开放图");
            That(AntonTcpProbe.Barrier(again, "恢复分队参与选图").Any(p => p is { Kind: 1, Opcode: 15, Body: [1] }),
                "新owner已是原普通分队成员，能收到共享选图通知");
        }
        await fixture.StopAsync();
        var saved = CompleteEvents(logs);
        That(LastReason(saved, "raid_rejoin_notice") == "835-tail-semantics-unknown", "835尾部在日志中标为unknown");
        That(saved.Any(row => row.GetProperty("Kind").GetString() == "raid_rejoin_installed" &&
            row.GetProperty("Detail").TryGetProperty("StateRestored", out var restored) && restored.GetBoolean()),
            "日志明确记录原能力安装和本服场景重放策略");
        That(saved.Any(row => row.GetProperty("Kind").GetString() == "raid_rejoin_rejected" &&
            row.GetProperty("Detail").GetProperty("Reason").GetString() == "duplicate-rejoin"), "重复重入有独立原因");
        if (inRun)
        {
            var entry = saved.Single(row => row.GetProperty("Kind").GetString() == "formal_dungeon_entry" &&
                row.GetProperty("Detail").GetProperty("Accepted").GetBoolean()).GetProperty("Detail");
            var restored = saved.First(row => row.GetProperty("Kind").GetString() == "raid_rejoin_installed" &&
                row.GetProperty("Detail").GetProperty("StateRestored").GetBoolean()).GetProperty("Detail");
            That(entry.GetProperty("RaidInstance").GetGuid() == restored.GetProperty("Instance").GetGuid() &&
                entry.GetProperty("Runs")[0].GetGuid() == restored.GetProperty("Run").GetGuid(),
                "恢复日志与真实入场日志的Battle/Run GUID完全相同，没有另建实例");
        }

        string RejoinState() => fixture.Snapshot() + RejoinAssets() +
            fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY 1,2") +
            fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY 1");

        string RejoinAssets() => fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM character_raid_reward_receipts ORDER BY character_id,instance_id,phase,kind");

        List<WirePacket> Rejoin(uint requestedKey, string why, bool accepted)
        {
            // 本服复用正式场景消费者；收齐ACK前后帧，不能由ReadAck吞掉提前通知。
            again.Send(668, Build(w => w.Write(requestedKey)));
            var received = AntonTcpProbe.Barrier(again, why);
            var acknowledgements = received.Where(p => p is { Kind: 1, Opcode: 668 }).ToArray();
            That(acknowledgements.Length == 1, why + " 恰好一个668应答");
            Bytes(acknowledgements[0].Body, accepted ? [1] : [0, 19, 0], why + " 应答只反映本次原能力安装结果");
            That(received.All(p => p is not { Kind: 0, Opcode: 30 } && p is not { Kind: 1, Opcode: 16 or 666 }),
                why + " 不伪造入场、重新开战或未确认加载");
            if (accepted && inRun)
                That(received.Count(p => p is { Kind: 0, Opcode: 27 }) == 1 &&
                    received.FindIndex(p => p is { Kind: 0, Opcode: 27 }) < received.FindIndex(p => p is { Kind: 0, Opcode: 28 }) &&
                    received.FindIndex(p => p is { Kind: 0, Opcode: 28 }) < received.FindIndex(p => p is { Kind: 0, Opcode: 29 }),
                    why + " 先27建立选图模块，再28安装DGN、29安装原房间");
            else That(received.All(p => p is not { Kind: 0, Opcode: 27 or 28 or 29 }), why + " 无新房间重放");
            return received;
        }
    }

    static void AwaitMemberLeft(TcpPeer leader, string why)
    {
        for (int at = 0; at < 256; at++)
        {
            var packet = leader.Read();
            if (packet is { Kind: 0, Opcode: 591 } && U32(packet.Body, 4) == 3)
            {
                AntonTcpProbe.Barrier(leader, why);
                return;
            }
        }
        throw new InvalidOperationException(why + "未观察到原团移除成员");
    }

    static void RejectChangedRun(TcpTestFixture fixture, TcpPeer leader, TcpPeer member, uint key,
        RaidDungeonCatalog catalog, byte[] info, byte[] firstMap, bool firstRoomCleared)
    {
        // 战斗中断线夹具已真实清房，恢复29必须反映空存活集；不能按入场旧演员再杀一次。
        if (firstRoomCleared)
            That(firstMap.Length >= 19 && firstMap[18] == 0, "过期Run用例从恢复29确认已清首房，不重杀旧怪物");
        else AntonTcpProbe.ClearRoom(leader, firstMap, new(), out _, out _, false, member, new());
        member.Dispose();
        // 原分队成员退出会发NOTI9/action2与NOTI26；见PartyProjection及PartyPackets.Host的023A2237读宽。
        // 先等断线事实再收齐后续帧，不能把本连接屏障当成另一连接已清理的证明。
        var disconnected = new List<WirePacket>();
        for (int at = 0; at < 256; at++)
        {
            var packet = leader.Read();
            disconnected.Add(packet);
            if (packet is { Kind: 0, Opcode: 591 } && U32(packet.Body, 4) == 3) break;
        }
        That(disconnected.Any(p => p is { Kind: 0, Opcode: 591 } && U32(p.Body, 4) == 3),
            "恢复成员再次断线后原团名册同步移除");
        disconnected.AddRange(AntonTcpProbe.Barrier(leader, "过期Run用例等待原成员断线"));
        // PartyPackets.Members先写count/pages及id/server/channel，InformationBlock的action位于正文偏移9。
        That(disconnected.Any(p => p is { Kind: 0, Opcode: 9, Body.Length: > 9 } && p.Body[9] == 2) &&
            disconnected.Any(p => p is { Kind: 0, Opcode: 26, Body: [0] }),
            "原分队仅余槽0的队长，发送成员增量及真实主机槽0");
        var path = AntonTcpProbe.Route(catalog, 210, info[7], firstMap[0], firstMap[1], info[8], info[9]);
        That(path.Length > 2, "重入过期用例使用210非Boss相邻房，不借最终通关改变阶段");
        var next = Move(checked((byte)path[1].X), checked((byte)path[1].Y));
        AntonTcpProbe.ClearRoom(leader, next, new(), out _, out _, false);
        var back = Move(firstMap[0], firstMap[1]);
        That(back[0] == firstMap[0] && back[1] == firstMap[1], "幸存者过房后返回已清原房，地图与当前存活集合再次相同");
        using var stale = Open(fixture, "antonrejoin1", gamePort: ChannelTcpTests.Endpoints(fixture)[19], channelNumber: 19);
        stale.Select();
        ReadNotice(stale);
        stale.Send(668, Build(w => w.Write(key)));
        Bytes(ReadAck(stale, 668), [0, 19, 0], "已错过房间进度的旧个人Run不能覆盖幸存者");
        That(AntonRaidTcpTests.Information(leader, key, 1).Members.All(m => m.Character != stale.Id),
            "仅有相同地图与存活集合不足以恢复，原Run进度不同仍不安装成员");
        That(fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10096296") is [[2L]],
            "恢复与过期拒绝都不重新扣票");

        byte[] Move(byte x, byte y)
        {
            var request = new byte[100]; request[0] = x; request[1] = y;
            leader.Send(45, request);
            var moved = AntonTcpProbe.Barrier(leader, "恢复过期用例过房");
            var map = moved.Single(p => p is { Kind: 0, Opcode: 29 }).Body;
            leader.Send(37, new byte[8]);
            AntonTcpProbe.Barrier(leader, "恢复过期用例加载");
            return map;
        }
    }

    static void AssertRejoinClean(TcpTestFixture fixture, TcpPeer again, string why)
    {
        That(fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10096296") is [[2L]],
            why + " 时断线和 668 不退票也不再扣");
        again.Send(833, Build(w => { w.Write((byte)0); w.Write(0u); w.Write(0x100u); w.Write(0u); w.Write(Name("")); }));
        That(AntonRaidTcpTests.Until(again, 0, 756).Body.Length >= 12, why + " 时重连后仍能 833 寻找队");
        again.Send(15, new byte[4]);
        var rejected = AntonTcpProbe.Barrier(again, "未恢复原团的选图拒绝");
        var acknowledgements = rejected.Where(packet => packet is { Kind: 1, Opcode: 15 }).ToArray();
        That(acknowledgements.Length == 1, why + " 时未授权选图恰有一次拒绝应答");
        Bytes(acknowledgements[0].Body, [0, 1, 0], why + " 时未恢复原Battle，选图沿原门禁精确拒绝");
        That(rejected.All(packet => packet is not { Kind: 0, Opcode: 3 or 23 or 24 or 27 or 28 or 29 or 30 or
                585 or 586 or 587 or 594 or 600 or 601 } && packet is not { Kind: 1, Opcode: 16 or 666 }),
            why + " 时拒绝前后不发布选图、场景、Run、开战或奖励成功通知");
        // 有授权的同角色正控保留在enabled分支：原668恢复后再次参加原分队15选图。
        // 正式新团664/669/666后的15/132与不扣票合同由AntonRaidTcpTests.Start覆盖。
    }

    static string? LastReason(JsonElement[] events, string kind) => events.Last(row => row.GetProperty("Kind").GetString() == kind)
        .GetProperty("Detail").GetProperty("Reason").GetString();

    static JsonElement[] CompleteEvents(string directory)
    {
        var events = new List<JsonElement>();
        foreach (var line in File.ReadLines(Path.Combine(directory, "server.jsonl")))
        {
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                events.Add(doc.RootElement.Clone());
            }
            catch (JsonException) { }
        }
        return events.ToArray();
    }

    static void QuietDrain(TcpPeer peer)
    {
        peer.Send(1278);
        ReadUntil(peer, 1, 1278);
    }

    static WirePacket ReadUntil(TcpPeer peer, byte kind, ushort opcode)
    {
        for (int at = 0; at < 256; at++)
        {
            var packet = peer.Read();
            if (packet.Kind == kind && packet.Opcode == opcode) return packet;
        }
        throw new InvalidOperationException($"没有 {kind}/{opcode}");
    }

    static byte[] ReadNotice(TcpPeer peer)
    {
        for (int at = 0; at < 24; at++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 0, Opcode: 835 }) return packet.Body;
        }
        throw new InvalidOperationException("重登后没有835");
    }

    static byte[] ReadAck(TcpPeer peer, ushort opcode)
    {
        for (int at = 0; at < 24; at++)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == opcode) return packet.Body;
        }
        throw new InvalidOperationException("没有668应答");
    }
}
