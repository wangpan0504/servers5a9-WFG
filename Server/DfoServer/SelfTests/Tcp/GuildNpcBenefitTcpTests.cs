using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class GuildNpcBenefitTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        Inventory.GuildNpcEmblemPoolTests.RunResources(resources.ItemResources);
        await using var fixture = new TcpTestFixture(source with
            { DevelopmentDungeonEntryProbe = Development.Dungeons.DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "guild-npc-benefits"), resources: resources);
        var repository = new CharacterRepository(fixture.Database); var owners = new AccountOwners();
        ushort id;
        using (var owner = owners.Acquire("10000", static () => { }))
        {
            var starter = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0));
            var coin = resources.ItemResources.Items.Get(490011648);
            starter = starter with { Gold = 2000000, Items = starter.Items.Add(new(0,
                new(InventoryListType.Main, ItemSlotBoundService.Range(coin.Kind, starter).Start), new(coin.Id, coin.Kind, 2000))) };
            id = repository.Create(owner, new("BuffMaster", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                DungeonState = CharacterDungeonState.NewOrdinary, Inventory = starter }).Id;
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        fixture.Sql("""
            INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix,public_funds)
            VALUES(98,'福利测试','g-benefits',$p0,1600000000,1000000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(98,$p0,1,1600000000);
            INSERT INTO guild_npc_progress(guild_id,npc_id,level,contract_until)
            VALUES(98,400001061,5,$p1),(98,400001062,2,$p1),(98,400001064,2,$p1),(98,400001065,4,$p1);
            """, id, now + 3600);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            Enter(peer);
            var ready = Drain(peer);
            That(ready.Any(packet => packet.Opcode == 487 && HasBuff(packet.Body, 1276)),
                "真实入城恢复守护石永久属性1276");
            peer.SendAtSequence(1738, 61000, Request(3, 400001062, 3001));
            peer.Expect(1, 1738, [1, 3, 0, 0, 0, 0, 0, 0, 0]);
            var common = Drain(peer);
            That(common.Any(packet => packet.Opcode == 1773 && U32(packet.Body, 0) == 3001) &&
                fixture.Query("SELECT public_funds FROM guilds WHERE guild_id=98") is [[970000L]],
                "真实action3扣公款并回显公共强化福利");
            peer.SendAtSequence(1738, 61000, Request(3, 400001062, 3001));
            peer.Expect(1, 1738, [1, 3, 0, 0, 0, 0, 0, 0, 0]); Drain(peer);
            That(fixture.Query("SELECT public_funds FROM guilds WHERE guild_id=98") is [[970000L]],
                "真实相同序号重放不重复扣公款");
            peer.Send(1738, Request(3, 400001062, 3002));
            peer.Expect(1, 1738, [1, 3, 0, 0, 0, 0, 0, 0, 0]);
            var personal = Drain(peer);
            That(personal.Any(packet => packet.Opcode == 1773 && U32(packet.Body, 480) == 3002) &&
                fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=490011648", id) is [[1800L]],
                "真实个人购买扣200实体硬币并回显个人福利");
            // 实机训练解锁请求的NPC字段为0，福利ID决定资源归属，旧前缀不参与授权。
            string beforeUnlock = fixture.Snapshot("SELECT gold,revision FROM characters WHERE id=$p0", id);
            foreach (var rejected in new[] { Request(3, 400001062, 5001), Request(3, 0, 3002), Request(3, 0, 999999) })
            {
                peer.Send(1738, rejected);
                peer.Expect(1, 1738, [1, 3, 0, 0, 0, 14, 0, 0, 0]);
            }
            That(fixture.Snapshot("SELECT gold,revision FROM characters WHERE id=$p0", id) == beforeUnlock,
                "零NPC仅用于训练解锁，错误归属和未知福利拒绝且不扣费");
            byte[] trainingUnlock = Convert.FromHexString(
                "A2AD66B7E4674606D0F71A00030300000000000000891300000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
            peer.SendAtSequence(1738, 61003, trainingUnlock);
            peer.Expect(1, 1738, [1, 3, 0, 0, 0, 0, 0, 0, 0]);
            var unlocked = Drain(peer);
            That(unlocked.Any(packet => packet.Opcode == 1774 && packet.Body.Length == 16 && packet.Body[0] == 2) &&
                fixture.Query("SELECT gold FROM characters WHERE id=$p0", id) is [[1700000L]] &&
                fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=490011648", id) is [[1600L]],
                "真实训练解锁同事务扣金币和硬币，1774回已解锁未选状态");
            peer.SendAtSequence(1738, 61003, trainingUnlock);
            peer.Expect(1, 1738, [1, 3, 0, 0, 0, 0, 0, 0, 0]); Drain(peer);
            That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", id) is [[1700000L]] &&
                fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=490011648", id) is [[1600L]],
                "实机零NPC训练请求重放不重复扣金币和公会币");
            peer.SendAtSequence(1738, 61004, Request(6, 0, 5001));
            peer.Expect(1, 1738, [1, 6, 0, 0, 0, 0, 0, 0, 0]);
            That(Drain(peer).Any(packet => packet.Opcode == 1774 && packet.Body[0] == 3),
                "真实action6选择已解锁训练并恢复选择状态3");
            string assets = fixture.Snapshot("SELECT id,revision,gold FROM characters");
            peer.SendAtSequence(1738, 61004, Request(6, 0, 5001));
            peer.Expect(1, 1738, [1, 6, 0, 0, 0, 0, 0, 0, 0]); Drain(peer);
            peer.Send(1738, Request(6, 0, 5002));
            peer.Expect(1, 1738, [1, 6, 0, 0, 0, 14, 0, 0, 0]); Drain(peer);
            That(assets == fixture.Snapshot("SELECT id,revision,gold FROM characters"),
                "选择重放和未解锁档位拒绝均不改资产");
            peer.Send(15, Convert.FromHexString("88130000"));
            That(Drain(peer).Any(packet => packet is { Kind: 1, Opcode: 15, Body: [1] }),
                "训练福利角色进入真实5000副本选择");
            peer.Send(16, Convert.FromHexString("881300000000000001FFFF0000000000000000000000000000"));
            var entered = Drain(peer);
            That(entered.Any(packet => packet.Opcode == 28 && U32(packet.Body) == 5000) &&
                fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=490011648", id) is [[1599L]],
                "真实入场费用事务额外扣所选一级训练1枚硬币");
            peer.Send(37, new byte[8]);
            var loaded = Drain(peer);
            That(loaded.Any(packet => packet.Opcode == 1774 && packet.Body[0] == 3) &&
                loaded.Any(packet => packet.Opcode == 487 && HasBuff(packet.Body, 1281) && U32(packet.Body, 9) is > 0 and <= 180) &&
                loaded.Any(packet => packet.Opcode == 487 && HasBuff(packet.Body, 1276)),
                "真实Actor重建恢复训练状态、剩余180秒效果及守护石属性");
            peer.Send(37, new byte[8]); Drain(peer);
            That(fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=490011648", id) is [[1599L]],
                "重复加载不再次扣训练币");
            peer.Send(42);
            var returned = Drain(peer);
            That(returned.Any(packet => packet.Opcode == 488 && packet.Body.Length == 5 && U32(packet.Body, 1) == 1281),
                "真实返回城镇清除本实例训练效果");
        }
        using (var peer = Open(fixture))
        {
            Enter(peer);
            var restored = Drain(peer);
            That(restored.Any(packet => packet.Opcode == 1773 && U32(packet.Body, 480) == 3002) &&
                restored.Any(packet => packet.Opcode == 1774 && packet.Body[0] == 3),
                "真实重连恢复个人购买及训练选择");
            byte[] craftRequest = Convert.FromHexString(
                "E4F61A0044F6F903CCF61A009C04000000000000005D1B00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
            peer.SendAtSequence(1738, 62001, craftRequest);
            peer.Expect(1, 1738, [1, 4, 0, 0, 0, 0, 0, 0, 0]);
            var crafting = Drain(peer);
            That(crafting.Any(packet => packet.Opcode == 1773 && Enumerable.Range(0, 50)
                    .Any(at => U32(packet.Body, 480 + at * 12) == 7005 && U32(packet.Body, 488 + at * 12) > now)) &&
                fixture.Query("SELECT gold FROM characters WHERE id=$p0", id) is [[1200000L]] &&
                fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=490011648", id) is [[1099L]] &&
                fixture.Query("SELECT count(*) FROM character_guild_npc_item_crafts WHERE state=0") is [[1L]],
                "真实炼金生产请求扣500硬币和50万金币，保存24小时个人生产并恢复倒计时");
            peer.SendAtSequence(1738, 62001, craftRequest);
            peer.Expect(1, 1738, [1, 4, 0, 0, 0, 0, 0, 0, 0]); Drain(peer);
            That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", id) is [[1200000L]], "炼金生产原序号重放不重复扣费");
            peer.Send(1738, Request(4, 0, 2009));
            peer.Expect(1, 1738, [1, 4, 0, 0, 0, 0, 0, 0, 0]);
            var teranium = Drain(peer);
            That(teranium.Any(packet => packet.Opcode == 1773 && Enumerable.Range(0, 50)
                    .Any(at => U32(packet.Body, 480 + at * 12) == 2009 && U32(packet.Body, 488 + at * 12) > now)) &&
                fixture.Query("SELECT gold FROM characters WHERE id=$p0", id) is [[1200000L]] &&
                fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=490011648", id) is [[699L]] &&
                fixture.Query("SELECT count(*) FROM character_guild_npc_item_crafts WHERE state=0") is [[2L]] &&
                fixture.Query("SELECT count(*) FROM character_guild_npc_craft_weeks") is [[1L]],
                "真实满级守护精灵2009扣400会员币不扣金币，与炼金并行并占本周");
            peer.Send(1738, Request(4, 0, 2009));
            peer.Expect(1, 1738, [1, 4, 0, 0, 0, 14, 0, 0, 0]); Drain(peer);
            That(fixture.Query("SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=490011648", id) is [[699L]],
                "同周第二次时空石生产回错误14且不扣费");
        }
        using (var peer = Open(fixture))
        {
            Enter(peer);
            var restoredCrafts = Drain(peer);
            That(restoredCrafts.Any(packet => packet.Opcode == 1773 && Enumerable.Range(0, 50)
                    .Any(at => U32(packet.Body, 480 + at * 12) == 7005)) &&
                restoredCrafts.Any(packet => packet.Opcode == 1773 && Enumerable.Range(0, 50)
                    .Any(at => U32(packet.Body, 480 + at * 12) == 2009)),
                "真实重连恢复炼金与时空石委托倒计时");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]], "福利TCP隔离库完整");
        Console.WriteLine($"PASS {Count - before} guild NPC benefit TCP assertions");
    }
    static bool HasBuff(byte[] body, uint id) => body.Length > 0 && body.Length == 1 + body[0] * 16 &&
        Enumerable.Range(0, body[0]).Any(at => U32(body, 1 + at * 16) == id);
    static byte[] Request(uint action, uint npc, params uint[] ids) => Build(writer =>
    {
        writer.Write(new byte[13]); writer.Write(action); writer.Write(npc);
        foreach (uint id in ids) writer.Write(id);
        writer.Write(new byte[48 - ids.Length * 4]);
    });
    static void Enter(TcpPeer peer)
    {
        peer.Send(8, [255, 255, 2]); peer.Send(4, Build(writer => writer.Write((ushort)0)));
    }
    static WirePacket[] Drain(TcpPeer peer)
    {
        peer.Send(156, GuildApplicationTests.Text("BuffBarrier"));
        var packets = new List<WirePacket>();
        for (int at = 0; at < 512; at++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 1, Opcode: 156 })
            {
                Bytes(packet.Body, [1], "福利请求查重哨兵完整完成"); return packets.ToArray();
            }
            packets.Add(packet);
        }
        throw new InvalidOperationException("Guild benefit request did not reach barrier");
    }
}
