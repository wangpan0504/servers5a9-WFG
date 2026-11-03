using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class GuildEconomyTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        const int giftId = 10153530;
        const int scrollId = 10161083;
        var gift = resources.ItemResources.Items.GuildMileageGift(giftId);
        var scroll = resources.ItemResources.Items.GuildDungeonClearScroll(scrollId);
        That(gift is { Amount: > 0 }, "当前95PVF的公会积分礼袋10153530按标签装配");
        That(scroll is { DungeonId: 8500, Increment: 1 }, "当前95PVF的伤城净化卷轴10161083按167装配");
        var items = resources.ItemResources.Items;
        foreach (var (scrollItem, dungeon, increment) in new (int Id, uint Dungeon, int Increment)[]
        {
            (10161078, 8504, 1), (10161079, 8507, 1), (10161080, 8505, 1),
            (10161081, 8503, 1), (10161082, 8508, 1), (10161083, 8500, 1),
            (490007011, 8504, 14), (490007012, 8507, 20), (490007013, 8505, 20),
            (490007014, 8503, 14), (490007015, 8508, 34), (490007016, 8500, 7),
            (10160833, 8504, 1), (10160834, 8507, 1), (10160835, 8505, 1),
            (10160836, 8503, 1), (10160837, 8508, 1), (10160838, 8500, 1),
        })
            That(items.GuildDungeonClearScroll(scrollItem) is { } row && row.DungeonId == dungeon && row.Increment == increment,
                "公会商店及10%档净化卷轴按同一167标签装配: " + scrollItem);
        That(items.GuildDungeonClearScroll(10155510) is null && items.GuildDungeonClearScroll(10155681) is null,
            "安德商店公会地下城延长券不是167净化卷轴");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "guild-economy"), resources: resources);
        var repository = new CharacterRepository(fixture.Database); var owners = new AccountOwners();
        ushort id;
        using (var owner = owners.Acquire("10000", static () => { }))
        {
            var inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0));
            var scrollKind = resources.ItemResources.Items.Get(scrollId).Kind;
            inventory = inventory with { Items = inventory.Items
                .Add(new(0, new(InventoryListType.Main, 68), new(giftId, ItemKind.Consumable, 2)))
                .Add(new(0, new(InventoryListType.Main, 69), new(10006240, ItemKind.Consumable, 2)))
                .Add(new(0, new(InventoryListType.Main, 70), new(scrollId, scrollKind, 2))) };
            id = repository.Create(owner, new("FWireMaster", resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = inventory }).Id;
        }
        fixture.Sql("""
            INSERT INTO guilds(guild_id,name,address,master_character_id,public_funds,created_at_unix)
            VALUES(94,'经济测试','f-wire',$p0,2000000,1600000000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(94,$p0,0,1600000000)
            """, id);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Send(8, [255, 255, 2]); peer.Send(4, Build(writer => writer.Write((ushort)0)));
            bool entered = false;
            for (int i = 0; i < 256; i++)
                if (peer.Read() is { Kind: 0, Opcode: 124 }) { peer.ReadWeeklyDungeon(); entered = true; break; }
            That(entered, "经济TCP完成真实选角与入城");
            Flush(peer);
            string gold = fixture.Snapshot("SELECT id,gold FROM characters ORDER BY id");
            peer.Send(761, Purchase(3)); Expect(peer, 761, [1]); Flush(peer);
            That(fixture.Query("SELECT public_funds FROM guilds WHERE guild_id=94") is [[1999800L]] &&
                fixture.Query("SELECT type,value,expires_unix>0 FROM guild_benefits WHERE guild_id=94") is [[1L, 10L, 1L]],
                "761经验福利真正扣200公共积分并写入有效截止");
            peer.Send(761, Purchase(16)); Expect(peer, 761, [1]); Flush(peer);
            That(fixture.Query("SELECT member_limit FROM guild_economy WHERE guild_id=94") is [[500L]],
                "容量扩容经真实761落为500");
            peer.Send(761, Purchase(18)); Expect(peer, 761, [0, 105, 0]);
            peer.Send(761, Purchase(11)); Expect(peer, 761, [1]); Flush(peer);
            That(fixture.Query("SELECT capacity FROM guild_cargo_state WHERE guild_id=94") is [[24L]],
                "仓库内容真实扩16格，保持E容量事实源");
            peer.Send(761, Purchase(2)); Expect(peer, 761, [1]);
            var updates = Flush(peer);
            var added = updates.Where(packet => packet is { Kind: 0, Opcode: 487 }).ToArray();
            That(added.Any(packet => packet.Body.Length >= 17 && packet.Body[0] >= 1 && BitConverter.ToUInt32(packet.Body, 1) == 167),
                "购买属性福利下发实际487实例167，而非只显示福利名称");
            peer.Send(761, Purchase(1)); Expect(peer, 761, [1]); Flush(peer);
            peer.Send(761, Purchase(1)); Expect(peer, 761, [0, 9, 0]);
            peer.Send(761, Purchase(13)); Expect(peer, 761, [1]); Flush(peer);
            That(fixture.Query("SELECT value,expires_unix FROM guild_benefits WHERE guild_id=94 AND type=7") is [[10L, 0L]],
                "95Days=-1传送次数作为真实公共计数持久化");
            long beforeGift = Convert.ToInt64(fixture.Query("SELECT public_funds FROM guilds WHERE guild_id=94")[0][0]);
            peer.Send(515, Build(writer =>
            {
                writer.Write((ushort)68); writer.Write((byte)0); writer.Write(0u); writer.Write(146u); writer.Write(0u); writer.Write(0u);
            }));
            Expect(peer, 515, [1, 68, 0, 0, 146, 0, 0, 0]); Flush(peer);
            using var readOwner = owners.Acquire("10000", static () => { });
            That(fixture.Query("SELECT public_funds FROM guilds WHERE guild_id=94") is [[var balance]] &&
                Convert.ToInt64(balance) == beforeGift + gift!.Amount &&
                repository.List(readOwner).Single().Inventory.Find(new(InventoryListType.Main, 68))?.Core.Count == 1,
                "515动作146同事务扣一袋并增加标签定义的公共积分");
            long fundsBeforeBook = Convert.ToInt64(fixture.Query("SELECT public_funds FROM guilds WHERE guild_id=94")[0][0]);
            peer.Send(515, Build(writer =>
            {
                writer.Write((ushort)69); writer.Write((byte)0); writer.Write(0u); writer.Write(145u); writer.Write(0u); writer.Write(0u);
            }));
            Expect(peer, 515, [1, 69, 0, 0, 145, 0, 0, 0]); Flush(peer);
            That(fixture.Query("SELECT experience,level,tier FROM guilds WHERE guild_id=94") is [[500L, 1L, 0L]] &&
                Convert.ToInt64(fixture.Query("SELECT public_funds FROM guilds WHERE guild_id=94")[0][0]) == fundsBeforeBook &&
                repository.List(readOwner).Single().Inventory.Find(new(InventoryListType.Main, 69))?.Core.Count == 1 &&
                fixture.Query("SELECT amount,after_experience-before_experience FROM guild_growth_item_receipts") is [[500L, 500L]],
                "真实515动作145扣一件500经验书并刷新公会成长，不混入公共积分礼袋");
            peer.Send(515, Build(writer =>
            {
                writer.Write((ushort)70); writer.Write((byte)0); writer.Write(0u); writer.Write(167u); writer.Write(0u); writer.Write(0u);
            }));
            Expect(peer, 515, [1, 70, 0, 0, 167, 0, 0, 0]);
            var afterScroll = Flush(peer);
            That(fixture.Query("SELECT dungeon_id,clear_count FROM guild_dungeon_progress WHERE guild_id=94") is [[8500L, 1L]] &&
                fixture.Query("SELECT effective_clear_units FROM guild_dungeon_effective_progress WHERE guild_id=94 AND dungeon_id=8500") is [[1L]] &&
                repository.List(readOwner).Single().Inventory.Find(new(InventoryListType.Main, 70))?.Core.Count == 1 &&
                afterScroll.Any(packet => packet is { Kind: 0, Opcode: 744 }),
                "真实515动作167扣一张伤城净化卷轴并增加有效通关单位，744随管理刷新");
            peer.Send(747, []);
            byte[] history = Expect(peer, 747).Body;
            uint count = BitConverter.ToUInt32(history, 1);
            That(history[0] == 1 && count == 7 && history.Length == 5 + 48 * count && history[39] == 8 &&
                BitConverter.ToInt32(history, 45) == gift!.Amount,
                "747返回真实最新礼袋收入与六笔内容消费，每条48字节");
            That(fixture.Snapshot("SELECT id,gold FROM characters ORDER BY id") == gold,
                "公会公共积分经济从未扣角色金币");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]], "经济链路结束库及外键完整");
        Console.WriteLine($"PASS {Count - before} 公会内容购买、属性投影及积分礼袋TCP断言");
    }
    static byte[] Purchase(uint content) => Build(writer => { writer.Write(content); writer.Write(0u); });
    static WirePacket Expect(TcpPeer peer, ushort opcode, byte[]? body = null)
    {
        for (int i = 0; i < 128; i++)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == opcode)
            {
                if (body is not null) Bytes(packet.Body, body, "公会经济确认内容严格匹配");
                return packet;
            }
            That(Refresh(peer, packet), "公会经济等待确认只允许明确的状态刷新");
        }
        throw new InvalidOperationException("公会经济确认超过帧预算");
    }
    static bool Refresh(TcpPeer peer, WirePacket packet)
    {
        if (packet.Kind == 0 && packet.Opcode == 744)
        {
            peer.CheckGuildDungeon(packet.Body);
            return true;
        }
        return packet.Kind == 0 && packet.Opcode is 2 or 58 or 70 or 127 or 151 or 487 or 488 ||
            packet.Kind == 1 && packet.Opcode is 140 or 141 or 744 or 1454;
    }
    static WirePacket[] Flush(TcpPeer peer)
    {
        peer.Send(156, Build(writer => { byte[] name = Game.Characters.CharacterName.Encoding.GetBytes("FEconBarrier"); writer.Write(name.Length); writer.Write(name); }));
        var rows = new List<WirePacket>();
        for (int i = 0; i < 128; i++)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == 156) { Bytes(packet.Body, [1], "经济后继请求哨兵"); return rows.ToArray(); }
            That(Refresh(peer, packet), "经济刷新只包含明确的公会、属性与账号视图"); rows.Add(packet);
        }
        throw new InvalidOperationException("公会经济刷新超过帧预算");
    }
}
