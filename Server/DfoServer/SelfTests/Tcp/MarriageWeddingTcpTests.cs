using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
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
        var clock = new Clock();
        var catalog = MarriageCatalog.Load(resources.Archive);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "marriage-wedding"), resources: resources, clock: clock);
        await fixture.StartAsync();
        foreach (byte type in new byte[] { 0, 1, 2 })
            await Ceremony(fixture, catalog, resources.Towns, clock, type, missing: false);
        await Ceremony(fixture, catalog, resources.Towns, clock, 2, missing: true);
        That(fixture.Query("PRAGMA foreign_key_check").Length == 0 && fixture.Query("PRAGMA integrity_check") is [["ok"]],
            "三档成功及付费缺席退款保持隔离库完整");
        Console.WriteLine($"PASS {Count - checks} marriage wedding TCP assertions");
    }

    static async Task Ceremony(TcpTestFixture fixture, MarriageCatalog catalog, TownCatalog towns, Clock clock, byte type, bool missing)
    {
        string suffix = type + (missing ? "fail" : "ok");
        string accountA = "weddinga" + suffix, accountB = "weddingb" + suffix;
        using var first = Open(fixture, accountA);
        using var second = Open(fixture, accountB);
        using var guest = Open(fixture, "weddingg" + suffix);
        first.Create("WedA" + suffix, 0); second.Create("WedB" + suffix, 1); guest.Create("WedG" + suffix, 2);
        fixture.Sql("UPDATE characters SET level=30,gold=10000000 WHERE id IN ($p0,$p1,$p2)", first.Id, second.Id, guest.Id);
        // 准备任务另有真实通关回归；本夹具冻结已完成准备的婚约，专测之后的正式网络入口。
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var ownerA = owners.Acquire(accountA, () => { }))
        using (var ownerB = owners.Acquire(accountB, () => { }))
        {
            var a = repository.LoadCharacter(ownerA, 0); var b = repository.LoadCharacter(ownerB, 0);
            var ticket = catalog.ChargedWeddingTickets.GetValueOrDefault(type);
            if (ticket.Count > 0)
                a = repository.Save(ownerA, a, a.Position, a.Return, new(a.Inventory, a.Inventory with
                { Items = a.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65), new(ticket.ItemId, ItemKind.Consumable, ticket.Count))) }, "marriage_fixture"));
            var record = MarriageWeddingRules.CreateEngagement(Guid.NewGuid(), (uint)(200 + type + (missing ? 10 : 0)),
                a.Id, b.Id, clock.GetUtcNow().ToUnixTimeSeconds()) with { PrepareClears = 5 };
            repository.CommitMarriageTogether(ownerA, ownerB, a, b, new(a.Inventory, a.Inventory, "marriage_fixture"),
                new(b.Inventory, b.Inventory, "marriage_fixture"), null, record);
        }
        foreach (ushort id in new[] { first.Id, second.Id })
            foreach (int quest in catalog.Quests)
                fixture.Sql("INSERT INTO character_completed_quests(character_id,quest_id,daily_period,answer) VALUES($p0,$p1,0,0)", id, quest);
        first.Select(); second.Select(); guest.Select();
        first.Barrier(); second.Barrier(); guest.Barrier();
        first.Send(1034, Structured(Build(w => { w.Write((uint)second.Id); w.Write((byte)2); w.Write(type); w.Write((byte)0); })));
        Bytes(first.Expect(1, 1034).Body, [1, .. Offer(second.Id, type)], "预约邀请ACK保留档位与等待选择");
        Bytes(second.Expect(0, 1000).Body, Offer(first.Id, type), "配偶接收同一档预约邀请");
        second.Send(1035, Structured(Build(w => { w.Write((uint)first.Id); w.Write((byte)2); w.Write((ushort)0); w.Write((byte)0); })));
        var bNotices = Drain(second); var aNotices = Drain(first);
        byte[] responseAck = [1, .. Offer(first.Id, 0), 0], responseNotice = [.. Offer(second.Id, type), 0];
        That(bNotices.Any(p => p.Kind == 1 && p.Opcode == 1035 && p.Body.AsSpan().SequenceEqual(responseAck)) &&
            aNotices.Any(p => p.Opcode == 1001 && p.Body.AsSpan().SequenceEqual(responseNotice)),
            "预约双确认后发布成功，ACK复述答复保留字，通知携带原邀请档位");
        var reserved = repository.FindMarriage(first.Id)!;
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
            var movieGuest = guest.Expect(0, 1010).Body;
            That(movieGuest.Length == 68 && U32(movieGuest, 0) == reserved.ContextId, "同场来宾也收到婚礼电影");
            var committed = repository.FindMarriage(first.Id)!;
            That(committed.State == 4 && committed.Wedding!.Gifts is [{ Settled: true, Text: "新婚快乐" }] &&
                fixture.Query("SELECT gold FROM mailbox_messages WHERE recipient_id=$p0 AND gold>0", first.Id) is [[2500L]] &&
                fixture.Query("SELECT gold FROM mailbox_messages WHERE recipient_id=$p0 AND gold>0", second.Id) is [[2500L]],
                "三档成功均原子结清祝福礼金，手续费及奇数分账按独立预期");
            That(fixture.Query("SELECT count(*) FROM mailbox_messages WHERE recipient_id IN ($p0,$p1,$p2)", first.Id, second.Id, guest.Id) is [[5L]],
                "夫妻礼金两封、档位礼包两封和来宾回礼一封各发一次");
            guest.Send(1036, money); Rejected(guest, 1036);
            Drain(first); Drain(second); Drain(guest);
            var destination = towns.Areas.Values.SelectMany(area => area.Default.Npcs.Where(npc => npc.Id == 20013)
                .Select(npc => new Location(area.Town, area.Area, checked((ushort)npc.X), checked((ushort)(npc.Y + 30))))).First();
            foreach (var peer in new[] { first, second, guest })
            {
                peer.Send(36, Area(destination.Town, destination.Area, destination.X, destination.Y, 0, 0));
                var returned = Drain(peer);
                That(returned.Any(p => p.Opcode == 23) && returned.Any(p => p.Opcode == 1002 && p.Body[0] == 1) &&
                    fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", peer.Id) is [[long town, long area, long x, long y]] &&
                    town == destination.Town && area == destination.Area && x == destination.X && y == destination.Y,
                    "电影结束真实CMD36将新人和访客保存回婚礼NPC并解除临时婚服");
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
            That(packet.Kind == 0 && packet.Opcode is 2 or 3 or 4 or 6 or 14 or 22 or 23 or 24 or 1001 or 1002 or 1003 or 1007 or 1016 ||
                packet.Kind == 1 && packet.Opcode is 1035 or 1041, $"婚礼切图只接收明确的投影和回执，实际{packet.Kind}/{packet.Opcode}");
            That(packet.Body.Length > 0, "婚礼流程通知必须有真实正文");
            if (packet.Opcode == 1002) That(packet.Body.Length == 71, "临时婚服71字节");
            if (packet.Opcode == 1007) That(packet.Body.Length == 79, "预约79字节");
            packets.Add(packet);
        }
        throw new InvalidDataException("婚礼流程屏障前通知超过上限");
    }
    static byte[] Structured(byte[] body) => [.. new byte[13], .. body];
    static byte[] Offer(ushort target, byte type) => Build(w => { w.Write(0); w.Write((uint)target); w.Write((byte)2); w.Write(type); w.Write((byte)0); });
    static void Rejected(TcpPeer peer, ushort opcode)
    {
        byte[] body = peer.Expect(1, opcode).Body;
        That(body is [1, ..] && body.Length >= 5 && unchecked((int)U32(body, 1)) < 0, "婚礼业务拒绝使用有符号错误码且连接继续可用");
    }
    sealed class Clock : TimeProvider
    {
        long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref seconds));
        public void Advance(TimeSpan value) => Interlocked.Add(ref seconds, checked((long)value.TotalSeconds));
    }
}
