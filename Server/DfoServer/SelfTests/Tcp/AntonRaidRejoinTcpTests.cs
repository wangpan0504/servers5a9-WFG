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
        leader.Send(664, Build(w => { w.Write((byte)0); w.Write(Name("Rejoin")); w.Write(new byte[4]); }));
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
        using var again = Open(fixture, "antonrejoin1", gamePort: endpoints[19], channelNumber: 19);
        again.Select();
        string logs = fixture.Options.LogDirectory;
        if (!enabled)
        {
            AntonRaidTcpTests.Drain(again);
            again.Send(668, Build(w => w.Write(key)));
            Bytes(AntonRaidTcpTests.Until(again, 1, 668).Body, [0, 19, 0], "关闭时668仍是原拒绝");
            AssertRejoinClean(fixture, again, "关闭");
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
        AssertClientNotifyGap(key, "首次668状态恢复");
        AssertClientNotifyGap(key, "重复668");
        AssertClientNotifyGap(key + 9, "错误key");
        That(RejoinAssets() == assets, "668首次、重复与错误key均不改变库存、资产审计或奖励回执");
        var members = AntonRaidTcpTests.Information(leader, key, 1).Members;
        That(members.Any(row => row.Character == again.Id) && members.Length == 2, "状态恢复使新owner回到原团名册，不建立新团");
        That(fixture.Query("SELECT town,area FROM characters WHERE name='Rej1'") is [[19L, 2L]],
            "状态恢复持久保存原候战室位置，不停在重登频道落点19/0");
        That(fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10096296") is [[2L]],
            "状态恢复和重复668没有再次扣票或退票");
        if (inRun)
        {
            var clean = AntonTcpProbe.Barrier(again, "恢复wire缺口屏障");
            That(clean.All(p => p.Opcode is not (27 or 28 or 29 or 30 or 585 or 586)),
                "UNIMPLEMENTED 恢复通知序列：状态恢复未擅自发送场景通知");
            if (clientNotifyGap)
            {
                That(AntonTcpProbe.Barrier(leader, "668后幸存者加载屏障").All(p => p is not { Kind: 0, Opcode: 30 }),
                    "UNIMPLEMENTED 客户端未自然加载，幸存者也不能收到伪造30放行");
            }
            else
            {
                again.Send(37, new byte[8]);
                var loaded = AntonTcpProbe.Barrier(again, "原Run重新确认加载");
                That(loaded.Any(p => p is { Kind: 0, Opcode: 30 }), "测试主动37仅验证原Run屏障能力，不代表客户端自然恢复");
                RejectChangedRun(fixture, leader, again, key, runCatalog!, runInfo!, runMap!);
            }
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
        That(saved.Any(row => row.GetProperty("Kind").GetString() == "raid_rejoin_rejected" &&
            row.GetProperty("Detail").TryGetProperty("StateRestored", out var restored) && restored.GetBoolean()),
            "日志明确记录状态已恢复，与wire未知分开");
        That(saved.Any(row => row.GetProperty("Kind").GetString() == "raid_rejoin_rejected" &&
            row.GetProperty("Detail").GetProperty("Reason").GetString() == "duplicate-rejoin"), "重复重入有独立原因");
        if (inRun)
        {
            var entry = saved.Single(row => row.GetProperty("Kind").GetString() == "formal_dungeon_entry" &&
                row.GetProperty("Detail").GetProperty("Accepted").GetBoolean()).GetProperty("Detail");
            var restored = saved.Single(row => row.GetProperty("Kind").GetString() == "raid_rejoin_rejected" &&
                row.GetProperty("Detail").GetProperty("StateRestored").GetBoolean()).GetProperty("Detail");
            That(entry.GetProperty("RaidInstance").GetGuid() == restored.GetProperty("Instance").GetGuid() &&
                entry.GetProperty("Runs")[0].GetGuid() == restored.GetProperty("Run").GetGuid(),
                "恢复日志与真实入场日志的Battle/Run GUID完全相同，没有另建实例");
        }

        string RejoinAssets() => fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM character_raid_reward_receipts ORDER BY character_id,instance_id,phase,kind");

        void AssertClientNotifyGap(uint requestedKey, string why)
        {
            // 证据见 Docs/协议/安徒恩重入/EVIDENCE.md：0234447C→0055D870在0x17直接返回；
            // 585/586/27/28/29/30的恢复顺序未证。必须收齐ACK前后帧，不能由ReadAck吞掉提前通知。
            again.Send(668, Build(w => w.Write(requestedKey)));
            var received = AntonTcpProbe.Barrier(again, why);
            var acknowledgements = received.Where(p => p is { Kind: 1, Opcode: 668 }).ToArray();
            That(acknowledgements.Length == 1, why + " 恰好一个668应答");
            Bytes(acknowledgements[0].Body, [0, 19, 0], why + " wire未知仍回已证错误19，不能以01冒充成功");
            That(received.All(p => p is not { Kind: 0, Opcode: 27 or 28 or 29 or 30 or 585 or 586 or 587 or 594 or 600 or 601 } &&
                p is not { Kind: 1, Opcode: 16 or 666 }), why + " UNIMPLEMENTED 恢复通知，ACK前后均无猜测场景、状态或奖励包");
        }
    }

    static void RejectChangedRun(TcpTestFixture fixture, TcpPeer leader, TcpPeer member, uint key,
        RaidDungeonCatalog catalog, byte[] info, byte[] firstMap)
    {
        AntonTcpProbe.ClearRoom(leader, firstMap, new(), out _, out _, false, member, new());
        member.Dispose();
        AntonRaidTcpTests.Until(leader, 0, 591);
        QuietDrain(leader);
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
        Bytes(AntonRaidTcpTests.Until(again, 1, 15).Body, [1], why + " 时同角色仍能打开候战室选图");
        AntonTcpProbe.Barrier(again, "重连后选图");
        again.Send(132);
        Bytes(AntonRaidTcpTests.Until(again, 1, 132).Body, [1], why + " 时同角色仍能退出选图回城");
        AntonTcpProbe.Barrier(again, "重连后回城");
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
