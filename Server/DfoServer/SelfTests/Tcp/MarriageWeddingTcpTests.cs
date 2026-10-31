using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Marriage;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class MarriageWeddingTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        Inventory.BoosterCharacterPoolTests.RunResources(resources.Towns, resources.ItemResources);
        var clock = new Clock();
        var catalog = MarriageCatalog.Load(resources.Archive);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "marriage-wedding"), resources: resources, clock: clock);
        await fixture.StartAsync();
        await Ceremony(fixture, catalog, resources, clock, 0, missing: false, prepared: false);
        foreach (byte type in new byte[] { 0, 1, 2 })
            await Ceremony(fixture, catalog, resources, clock, type, missing: false);
        await Ceremony(fixture, catalog, resources, clock, 2, missing: true);
        That(fixture.Query("PRAGMA foreign_key_check").Length == 0 && fixture.Query("PRAGMA integrity_check") is [["ok"]],
            "三档成功及付费缺席退款保持隔离库完整");
        Console.WriteLine($"PASS {Count - checks} marriage wedding TCP assertions");
    }

    static async Task Ceremony(TcpTestFixture fixture, MarriageCatalog catalog, TcpTestResources resources, Clock clock, byte type, bool missing,
        bool prepared = true)
    {
        var towns = resources.Towns;
        string suffix = type + (!prepared ? "quest" : missing ? "fail" : "ok");
        string accountA = "weddinga" + suffix, accountB = "weddingb" + suffix;
        using var first = Open(fixture, accountA);
        using var second = Open(fixture, accountB);
        using var guest = Open(fixture, "weddingg" + suffix);
        first.Create("WedA" + suffix, 0); second.Create("WedB" + suffix, 1); guest.Create("WedG" + suffix, type == 2 ? (byte)14 : (byte)2);
        fixture.Sql("UPDATE characters SET level=30,gold=10000000 WHERE id IN ($p0,$p1,$p2)", first.Id, second.Id, guest.Id);
        if (type == 2) fixture.Sql("UPDATE characters SET level=55,grow_type=1 WHERE id=$p0", guest.Id);
        // 准备任务另有真实通关回归；本夹具冻结已完成准备的婚约，专测之后的正式网络入口。
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        if (type == 2)
        {
            using var guestOwner = owners.Acquire("weddingg" + suffix, () => { });
            var role = repository.LoadCharacter(guestOwner, 0);
            repository.Save(guestOwner, role, role.Position, role.Return, new(role.Inventory, role.Inventory with
            { Items = role.Inventory.Items.Add(new(0, new(InventoryListType.Main, 68),
                InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(10002631), utcNow: clock.GetUtcNow()))) }, "marriage_fixture"));
        }
        using (var ownerA = owners.Acquire(accountA, () => { }))
        using (var ownerB = owners.Acquire(accountB, () => { }))
        {
            var a = repository.LoadCharacter(ownerA, 0); var b = repository.LoadCharacter(ownerB, 0);
            var ticket = catalog.ChargedWeddingTickets.GetValueOrDefault(type);
            if (ticket.Count > 0)
                a = repository.Save(ownerA, a, a.Position, a.Return, new(a.Inventory, a.Inventory with
                { Items = a.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65), new(ticket.ItemId, ItemKind.Consumable, ticket.Count))) }, "marriage_fixture"));
            var record = MarriageWeddingRules.CreateEngagement(Guid.NewGuid(), (uint)(200 + type + (!prepared ? 20 : missing ? 10 : 0)),
                a.Id, b.Id, clock.GetUtcNow().ToUnixTimeSeconds()) with { PrepareClears = type == 0 ? 5 : 0 };
            repository.CommitMarriageTogether(ownerA, ownerB, a, b, new(a.Inventory, a.Inventory, "marriage_fixture"),
                new(b.Inventory, b.Inventory, "marriage_fixture"), null, record);
        }
        foreach (ushort id in new[] { first.Id, second.Id })
            foreach (int quest in catalog.Quests)
                if (prepared || id == first.Id || quest != catalog.Quests[^1])
                    fixture.Sql("INSERT INTO character_completed_quests(character_id,quest_id,daily_period,answer) VALUES($p0,$p1,0,0)", id, quest);
        // 原生离堂回39/0；夹具同时满足该普通地区的真实准入，不绕过旅行门禁。
        foreach (ushort id in new[] { first.Id, second.Id, guest.Id })
            foreach (int quest in towns.Areas[(39, 0)].Quests)
                fixture.Sql("INSERT OR IGNORE INTO character_completed_quests(character_id,quest_id,daily_period,answer) VALUES($p0,$p1,0,0)", id, quest);
        first.Select(); second.Select(); guest.Select();
        first.Barrier(); second.Barrier(); guest.Barrier();
        byte[] hallExit = Convert.FromHexString("27007508B40005000000000000000000");
        if (type == 0 && prepared)
        {
            var before = fixture.Snapshot();
            guest.Send(36, hallExit);
            That(!Drain(guest).Any(p => p.Opcode is 23 or 1002) && fixture.Snapshot() == before,
                "普通地区不能借真实礼堂离场请求取得切图权限");
        }
        if (type == 2 && !missing)
        {
            foreach (var peer in new[] { first, second })
            {
                peer.Send(2, Build(w => { w.Write((byte)5); w.Write(0x0100007fu); w.Write(0x0100007fu); w.Write((ushort)(30100 + peer.Id)); w.Write(512u); }));
                PartyDungeonTcpTests.Drain(peer);
            }
            first.Send(10, Build(w => { w.Write(second.Id); w.Write((byte)0); w.Write(0u); w.Write(new byte[6]); }));
            PartyDungeonTcpTests.Drain(first); PartyDungeonTcpTests.Drain(second);
            second.Send(11, Build(w => { w.Write(first.Id); w.Write((byte)0); w.Write(0u); }));
            That(PartyDungeonTcpTests.Drain(second).Any(p => p is { Kind: 0, Opcode: 9 }) &&
                PartyDungeonTcpTests.Drain(first).Any(p => p is { Kind: 0, Opcode: 9 }), "原生婚礼请求在正式双人组队中验证");
            PartyDungeonTcpTests.Drain(guest);
        }
        byte[] request = [.. Convert.FromHexString("9CF71A00000000005F00000070"),
            .. Build(w => { w.Write((uint)second.Id); w.Write((byte)2); w.Write(type); w.Write((byte)0); })];
        byte[] response = [.. Convert.FromHexString("70F81A00094CD704FFFFFFFFE0"),
            .. Build(w => { w.Write((uint)first.Id); w.Write((byte)2); w.Write((ushort)0); w.Write((byte)0); })];
        first.Send(1034, request);
        Bytes(first.Expect(1, 1034).Body, [1, .. Offer(second.Id, type)], "预约邀请ACK保留档位与等待选择");
        Bytes(second.Expect(0, 1000).Body, Offer(first.Id, type), "配偶接收同一档预约邀请");
        if (type == 2 && !missing)
        {
            guest.Send(1035, response); Rejected(guest, 1035);
            second.Send(1035, Structured(Build(w => { w.Write((uint)guest.Id); w.Write((byte)2); w.Write((ushort)0); w.Write((byte)0); })));
            Bytes(second.Expect(1, 1035).Body, Answer(-2, guest.Id, 0, ack: true), "无邀请者和错对象回应不能消费真实配偶的邀请");
            first.Send(1034, request); Rejected(first, 1034);
            That(repository.FindMarriage(first.Id) is { State: 1, Wedding: null }, "邀请未决时拒绝重复请求，不预约或扣费");
            clock.Advance(TimeSpan.FromSeconds(61));
            second.Send(1035, response);
            Bytes(second.Expect(1, 1035).Body, Answer(-2, first.Id, 0, ack: true), "过期确认保留原预约操作与对象");
            Bytes(first.Expect(0, 1001).Body, Answer(-2, second.Id, type, ack: false), "过期确认通知原发起人关闭等待窗口");
            That(repository.FindMarriage(first.Id) is { State: 1, Wedding: null }, "过期确认不产生预约");
            first.Send(1034, request); first.Expect(1, 1034); second.Expect(0, 1000);
        }
        second.Send(1035, response);
        if (!prepared)
        {
            Bytes(second.Expect(1, 1035).Body, Answer(-1, first.Id, 0, ack: true), "缺少准备任务时错误回执仍保留预约操作，不能被客户端静默忽略");
            Bytes(first.Expect(0, 1001).Body, Answer(-1, second.Id, type, ack: false), "确认失败也通知原发起人关闭等待窗口");
            That(repository.FindMarriage(first.Id) is { State: 1, Wedding: null, PrepareClears: 5 } &&
                fixture.Query("SELECT gold FROM characters WHERE id=$p0", first.Id) is [[10000000L]], "通关计数不能代替双方准备任务，拒绝不扣费");
            return;
        }
        var bNotices = Drain(second); var aNotices = Drain(first);
        byte[] responseAck = [1, .. Offer(first.Id, 0), 0], responseNotice = [.. Offer(second.Id, type), 0];
        That(bNotices.Any(p => p.Kind == 1 && p.Opcode == 1035 && p.Body.AsSpan().SequenceEqual(responseAck)) &&
            aNotices.Any(p => p.Opcode == 1001 && p.Body.AsSpan().SequenceEqual(responseNotice)),
            "预约双确认后发布成功，ACK复述答复保留字，通知携带原邀请档位");
        var reserved = repository.FindMarriage(first.Id)!;
        That(reserved.PrepareClears == (type == 0 ? 5 : 0), "预约依据双方已完成任务，不要求或回填额外通关计数");
        var wedding = reserved.Wedding!;
        foreach (var notices in new[] { aNotices, bNotices })
        {
            That(notices.Count(p => p.Opcode == 1007) == 1 && notices.Any(p => p.Opcode == 1002 && p.Body[0] == 0 && p.Body[1] == type &&
                U32(p.Body, 62) == wedding.HallArea && U32(p.Body, 67) == wedding.ServerIndex), "每档预约回放倒计时并在入场后应用本档临时婚服");
        }
        That(fixture.Query("SELECT town,area,gold FROM characters WHERE id=$p0", first.Id) is [[39L, long hall, long gold]] &&
            hall == wedding.HallArea && gold == 10_000_000 - catalog.Ceremonies[type].Gold &&
            wedding.StartsAt == clock.GetUtcNow().ToUnixTimeSeconds() + 300 && reserved.State == 2,
            "预约保存真实礼堂、费用及300秒开始时间，未提前成婚");

        first.Send(1040, Structured(Build(w => { w.Write((byte)0); w.Write((uint)guest.Id); w.Write((ushort)0); w.Write((byte)0); })));
        first.Expect(1, 1040, Build(w => { w.Write((byte)1); w.Write(0); w.Write((ushort)(catalog.Ceremonies[type].LettersPerPersonRaw - 1)); }));
        var invitation = guest.Expect(0, 1008).Body;
        That(invitation.Length == 82 && U32(invitation, 0) == reserved.ContextId && U32(invitation, 4) == wedding.HallArea,
            "实际请帖投递给在线来宾并指向当前婚礼");
        byte[] entry = Structured(Build(w => { w.Write(reserved.ContextId); w.Write((uint)wedding.HallArea); w.Write((byte)0); }));
        guest.Send(1041, entry); Rejected(guest, 1041);
        const uint gift = 10003;
        byte[] text = DfoServer.Game.Characters.CharacterName.Encoding.GetBytes("新婚快乐");
        byte[] money = Build(w => { w.Write(reserved.ContextId); w.Write(gift); w.Write((uint)text.Length); w.Write(text); });
        guest.Send(1036, money); guest.Expect(0, 14);
        guest.Expect(1, 1036, Build(w => { w.Write((byte)1); w.Write(0); w.Write(10_000_000u - gift); }));
        guest.Send(1036, money); Rejected(guest, 1036);
        guest.Send(1041, entry);
        var guestNotices = Drain(guest); Drain(first); Drain(second);
        That(guestNotices.Any(p => p.Kind == 1 && p.Opcode == 1041 && p.Body.AsSpan().SequenceEqual(new byte[] { 1, 0, 0, 0, 0 })) &&
            fixture.Query("SELECT town,area,gold FROM characters WHERE id=$p0", guest.Id) is [[39L, long guestHall, 9989997L]] && guestHall == wedding.HallArea,
            "来宾送礼后正式入场，重发礼金不重复扣款");
        That(guestNotices.Any(p => p is { Kind: 0, Opcode: 14 }) &&
            repository.FindMarriage(first.Id)!.Wedding!.Invitations.Single(row => row.CharacterId == guest.Id).CeremonyGiftsReceived,
            "入场成功后下发实际礼炮库存差量，并保存当前请帖的领取凭据");
        foreach (var item in catalog.CeremonyGifts)
            That(fixture.Query("SELECT COALESCE(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items " +
                "WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1", guest.Id, item.ItemId) is [[long count]] &&
                count == item.Count, "入场礼数量严格采用PVF，不复用婚礼结束回礼");
        if (type == 0)
        {
            foreach (var item in catalog.CeremonyGifts)
            {
                That(resources.ItemResources.Items.SharedEffect(item.ItemId) is { CoolTime: 5000,
                    Outcomes: [{ Index: 0, Weight: 100000, BoosterId: 10002868 }] }, "当前两类礼炮对应同一奖励礼包及原生特效结果0");
                ushort slot = checked((ushort)Convert.ToInt64(fixture.Query("SELECT slot_index FROM character_inventory_slots s " +
                    "JOIN character_inventory_items i ON i.item_uid=s.item_uid WHERE s.character_id=$p0 " +
                    "AND json_extract(i.item_core,'$.ItemId')=$p1", guest.Id, item.ItemId).Single()[0]));
                byte[] use = Build(w => { w.Write(item.ItemId); w.Write(slot); });
                guest.Send(251, use);
                guest.Expect(1, 251, Build(w => { w.Write((byte)1); w.Write(slot); w.Write((byte)0); }));
                byte[] effect = Build(w => { w.Write(guest.Id); w.Write(item.ItemId); w.Write((byte)0); });
                foreach (var peer in new[] { guest, first, second }) peer.Expect(0, 219, effect);
                That(Drain(guest).Any(p => p is { Kind: 0, Opcode: 14 }) &&
                    fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 " +
                        "AND json_extract(item_core,'$.ItemId')=$p1", guest.Id, item.ItemId) is [[0L]],
                    "礼炮按一次真实消费并同步奖池产物，未把无名礼包留作背包实物");
                string unchanged = fixture.Snapshot();
                guest.Send(251, use); guest.Expect(1, 251, [0, 17, 0]); guest.Barrier();
                That(fixture.Snapshot() == unchanged, "旧源槽重放不消费奖励或重复产生礼炮效果");
            }
            var rewards = fixture.Query("SELECT json_extract(item_core,'$.ItemId'),json_extract(item_core,'$.Value') " +
                "FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId') IN (10002912,10002623)", guest.Id);
            That(rewards.Sum(row => Convert.ToInt64(row[1]) / (Convert.ToInt32(row[0]) == 10002623 ? 3 : 1)) == 2 &&
                fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 " +
                    "AND json_extract(item_core,'$.ItemId')=10002868", guest.Id) is [[0L]],
                "两次礼炮准确抽取两次当前PVF礼包池，不给客户端额外中间盒子");
        }
        guest.Send(1041, entry);
        That(!Drain(guest).Any(p => p is { Kind: 0, Opcode: 14 }), "重复入场不会再次发放入场礼");
        Drain(first); Drain(second);
        if (type == 2)
        {
            byte[] open = Build(w => { w.Write((ushort)68); w.Write(1); });
            byte[] expectedAck = Build(w =>
            {
                w.Write((byte)1); w.Write((byte)0); w.Write(10002631); w.Write((short)68);
                w.Write(0); w.Write(0); w.Write((ushort)5);
                foreach (var row in new[] { (490700962, 1), (415600000, 1), (10002901, 100), (10002611, 1), (10002612, 1) })
                { w.Write(row.Item1); w.Write(row.Item2); }
            });
            guest.Send(160, open);
            var updates = Drain(guest);
            Bytes(updates.Single(packet => packet is { Kind: 1, Opcode: 160 }).Body, expectedAck,
                "职业礼包成功回执包含本职业完整五项奖励");
            int ackIndex = updates.FindIndex(packet => packet is { Kind: 1, Opcode: 160 });
            That(!updates.Take(ackIndex).Any(packet => packet.Opcode == 14),
                "既有场景目录更新允许先到，当前开箱库存差量必须在ACK之后");
            That(updates.Any(packet => packet is { Kind: 0, Opcode: 14 } && packet.Body[0] == 0) &&
                updates.Any(packet => packet is { Kind: 0, Opcode: 14 } && packet.Body[0] == 1),
                "现场相同的女圣职者婚礼盒请求成功，随后同步普通道具和武器装扮容器");
            That(fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 " +
                "AND json_extract(item_core,'$.ItemId')=10002631", guest.Id) is [[0L]] &&
                fixture.Query("SELECT count(*) FROM inventory_avatar_details a JOIN character_inventory_items i ON i.item_uid=a.item_uid " +
                    "WHERE i.character_id=$p0 AND json_extract(i.item_core,'$.ItemId')=415600000", guest.Id) is [[1L]],
                "源盒实际消费，武器装扮核心与详情共同入库");
            string snapshot = fixture.Snapshot();
            guest.Send(160, open);
            var replay = Drain(guest);
            Bytes(replay.Single(packet => packet is { Kind: 1, Opcode: 160 }).Body, [0, 19, 0],
                "耗尽后的开箱请求按原生失败回执收尾");
            That(!replay.Any(packet => packet.Opcode == 14), "拒绝不发布获得差量");
            That(snapshot == fixture.Snapshot(), "耗尽源槽重放不会再发一套婚礼奖励");
        }
        if (missing)
        {
            second.Dispose();
            first.Expect(0, 6, Build(w => w.Write(second.Id)));
            guest.Expect(0, 6, Build(w => w.Write(second.Id)));
            // 等待真实离场通知后再跨过开场时刻，不能把请求屏障当成另一连接清理屏障。
            Drain(first); Drain(guest);
        }
        clock.Advance(TimeSpan.FromSeconds(missing ? 361 : 300));
        if (missing)
        {
            first.Expect(0, 1003);
            That(repository.FindMarriage(first.Id) is { State: 1, Wedding.State: 3 }, "缺席取消预约并保留婚约");
            var refunded = fixture.Query("SELECT recipient_id,gold FROM mailbox_messages WHERE recipient_id IN ($p0,$p1) ORDER BY recipient_id", first.Id, guest.Id);
            That(refunded.Any(row => Convert.ToUInt16(row[0]) == guest.Id && Convert.ToUInt32(row[1]) == gift) &&
                fixture.Query("SELECT count(*) FROM mailbox_attachments a JOIN mailbox_messages m ON m.mail_id=a.mail_id WHERE m.recipient_id=$p0", first.Id) is [[1L]],
                "付费婚礼缺席同时退回真实券和来宾本金");
        }
        else
        {
            foreach (var peer in new[] { first, second })
            {
                var info = peer.Expect(0, 1003).Body;
                That(info.Length == 15 && info[4] == 4 && U32(info, 6) == wedding.StartsAt, "开场才发布已婚与真实婚期");
                peer.Expect(0, 1018); peer.Expect(0, 1011);
                var movie = peer.Expect(0, 1010).Body;
                That(movie.Length == 68 && U32(movie, 0) == reserved.ContextId && U32(movie, 4) == wedding.HallArea, "新人电影通知匹配本场礼堂");
            }
            var guestMovie = guest.Read();
            int precedingUpdates = 0;
            while (guestMovie is { Kind: 0, Opcode: 9 or 1016 })
            {
                That(++precedingUpdates <= 8, "来宾电影前的队伍与新人属性更新数量有界");
                if (guestMovie.Opcode == 9)
                    That(guestMovie.Body is [1, ..], "组队新人状态变化发布正式队伍目录更新");
                else That(guestMovie.Body.Length == 14 && U32(guestMovie.Body, 0) != guest.Id,
                    "新人属性变化仍使用远端Actor的14字节婚姻投影");
                guestMovie = guest.Read();
            }
            That(guestMovie is { Kind: 0, Opcode: 1010 }, "来宾在队伍目录更新后仍须接收本场电影");
            var movieGuest = guestMovie.Body;
            That(movieGuest.Length == 68 && U32(movieGuest, 0) == reserved.ContextId, "同场来宾也收到婚礼电影");
            var committed = repository.FindMarriage(first.Id)!;
            That(committed.State == 4 && committed.Wedding!.Gifts is [{ Settled: true, Text: "新婚快乐" }] &&
                fixture.Query("SELECT gold FROM mailbox_messages WHERE recipient_id=$p0 AND gold>0", first.Id) is [[2500L]] &&
                fixture.Query("SELECT gold FROM mailbox_messages WHERE recipient_id=$p0 AND gold>0", second.Id) is [[2500L]],
                "三档成功均原子结清祝福礼金，手续费及奇数分账按独立预期");
            That(fixture.Query("SELECT count(*) FROM mailbox_messages WHERE recipient_id IN ($p0,$p1,$p2) AND delivery_key LIKE $p3",
                first.Id, second.Id, guest.Id, $"marriage:{reserved.Id:N}:{wedding.Id:N}:%") is [[5L]],
                "夫妻礼金两封、档位礼包两封和来宾回礼一封各发一次");
            guest.Send(1036, money); Rejected(guest, 1036);
            Drain(first); Drain(second); Drain(guest);
            var destination = towns.Areas.Values.SelectMany(area => area.Default.Npcs.Where(npc => npc.Id == 20013)
                .Select(npc => new Location(area.Town, area.Area, checked((ushort)npc.X), checked((ushort)(npc.Y + 30))))).First();
            if (type == 0)
            {
                foreach (int offset in new[] { 0, 1, 2, 4, 7, 9, 11, 15 })
                {
                    byte[] invalid = (byte[])hallExit.Clone();
                    invalid[offset] ^= 1;
                    var before = fixture.Snapshot();
                    first.Send(36, invalid);
                    That(!Drain(first).Any(p => p.Opcode is 23 or 1002) && fixture.Snapshot() == before,
                        "礼堂离场不能更改目标地区、坐标、来源、NPC或路径类型");
                }
            }
            foreach (var peer in new[] { first, second, guest })
            {
                bool movieReturn = type == 0 && peer == first;
                var target = movieReturn ? destination : new Location(39, 0, 2165, 180);
                peer.Send(36, movieReturn ? Area(target.Town, target.Area, target.X, target.Y, 0, 0) : hallExit);
                var returned = Drain(peer);
                That(returned.Any(p => p.Opcode == 23) && returned.Any(p => p.Opcode == 1002 && p.Body[0] == 1) &&
                    fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", peer.Id) is [[long town, long area, long x, long y]] &&
                    town == target.Town && area == target.Area && x == target.X && y == target.Y,
                    "电影结束及大祭司真实CMD36将新人和访客保存回普通地区并解除临时婚服");
                var beforeReplay = fixture.Snapshot();
                peer.Send(36, hallExit);
                That(!Drain(peer).Any(p => p.Opcode is 23 or 1002) && fixture.Snapshot() == beforeReplay,
                    "离堂后重放相同请求不再保存或重复发布婚服清理");
            }
            Drain(first); Drain(second); Drain(guest);
        }
        int mailCount = fixture.Query("SELECT count(*) FROM mailbox_messages").Select(row => Convert.ToInt32(row[0])).Single();
        await Task.Delay(1100);
        That(Convert.ToInt32(fixture.Query("SELECT count(*) FROM mailbox_messages").Single()[0]) == mailCount, "下一次调度不重发成功奖励或退款");
    }

    static List<WirePacket> Drain(TcpPeer peer)
    {
        peer.SendBarrier();
        List<WirePacket> packets = [];
        for (int index = 0; index < 64; index++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 1, Opcode: 1347 }) { Bytes(packet.Body, [1], "婚礼流程屏障成功"); return packets; }
            if(packet is {Kind:0,Opcode:411})
            {
                peer.ReadChatEmoticons(packet);packets.Add(packet);continue;
            }
            That(packet.Kind == 0 && packet.Opcode is 2 or 3 or 4 or 6 or 9 or 11 or 14 or 22 or 23 or 24 or 26 or 153 or 1001 or 1002 or 1003 or 1007 or 1016 ||
                packet.Kind == 1 && packet.Opcode is 160 or 1035 or 1041, $"婚礼切图只接收明确的投影和回执，实际{packet.Kind}/{packet.Opcode}");
            That(packet.Body.Length > 0, "婚礼流程通知必须有真实正文");
            if (packet.Opcode == 1002) That(packet.Body.Length == 71, "临时婚服71字节");
            if (packet.Opcode == 1007) That(packet.Body.Length == 79, "预约79字节");
            if (packet.Opcode == 9) That(packet.Body[0] == 1, "双人预约切图仍发布正式组队信息");
            if (packet.Opcode == 26) That(packet.Body.Length == 1, "组队主机通知保持单字节");
            packets.Add(packet);
        }
        throw new InvalidDataException("婚礼流程屏障前通知超过上限");
    }
    static byte[] Structured(byte[] body) => [.. new byte[13], .. body];
    static byte[] Offer(ushort target, byte type) => Build(w => { w.Write(0); w.Write((uint)target); w.Write((byte)2); w.Write(type); w.Write((byte)0); });
    static byte[] Answer(int result, ushort target, byte type, bool ack) => Build(w =>
    {
        if (ack) w.Write((byte)1);
        w.Write(result); w.Write((uint)target); w.Write((byte)2); w.Write(type); w.Write((byte)0); w.Write((byte)0);
    });
    static void Rejected(TcpPeer peer, ushort opcode)
    {
        byte[] body = peer.Expect(1, opcode).Body;
        That(body is [1, ..] && body.Length >= 5 && unchecked((int)U32(body, 1)) < 0, "婚礼业务拒绝使用有符号错误码且连接继续可用");
    }
    sealed class Clock : TimeProvider
    {
        long seconds = new DateTimeOffset(DateTime.Today.AddHours(12)).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref seconds));
        public void Advance(TimeSpan value) => Interlocked.Add(ref seconds, checked((long)value.TotalSeconds));
    }
}
