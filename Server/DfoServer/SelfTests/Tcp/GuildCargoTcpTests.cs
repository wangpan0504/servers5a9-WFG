using DfoServer.Game.Accounts;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class GuildCargoTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        var equipment = PvfScript.List(resources.Archive.ReadText("equipment/equipment.lst"));
        var stackable = PvfScript.List(resources.Archive.ReadText("stackable/stackable.lst"));
        var catalog = new GuildCargoCatalog(resources.Archive, equipment, stackable);
        var hall = resources.Towns.Areas.Values.OrderBy(area => area.Town).ThenBy(area => area.Area).First(area =>
            GuildCargoCatalog.IsHall(area.Default.Path) && area.Default.Error is null && area.Default.Npcs.Any(npc => catalog.StorageNpcs.Contains(npc.Id)));
        var bounds = hall.Default.Bounds.First(region => region.Quests.Length == 0 && region.Conditions.Length == 0).Bounds;
        var position = new Location(hall.Town, hall.Area, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        var street = GuildHallTcpTests.FindStreet(resources.Towns, hall);
        var template = stackable.Keys.Order().Select(id =>
        {
            try { return resources.ItemResources.Items.Get(id); }
            catch (InvalidDataException) { return null; }
        }).First(candidate => candidate is { Kind: ItemKind.Material, StackLimit: >= 20 } &&
            GuildCargoRules.Eligible(new(1, new(InventoryListType.Main, 121), new(candidate.Id, candidate.Kind, 20)), candidate, catalog.Allows(candidate.Id),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()))!;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "guild-cargo"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        ushort masterId, peerId, foreignId;
        long originalUid;
        using (var masterOwner = owners.Acquire("10000", () => { }))
        using (var peerOwner = owners.Acquire("20001", () => { }))
        using (var foreignOwner = owners.Acquire("20002", () => { }))
        {
            var starter = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0));
            var inventory = starter with { Items = starter.Items.Add(new(0, new(InventoryListType.Main, 121), new(template.Id, template.Kind, 20))) };
            var masterCharacter = repository.Create(masterOwner, new("CargoWireA", position, street)
                { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = inventory });
            masterId = masterCharacter.Id;
            originalUid = masterCharacter.Inventory.Find(new(InventoryListType.Main, 121))!.Uid;
            peerId = repository.Create(peerOwner, new("CargoWireB", position, street)
                { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter }).Id;
            foreignId = repository.Create(foreignOwner, new("CargoWireC", position, street)
                { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter }).Id;
        }
        fixture.Sql("""
            INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
            VALUES(71,'Cargo wire','cargo-wire',$p0,1600000000),(72,'Foreign wire','cargo-foreign',$p2,1600000000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix)
            VALUES(71,$p0,0,1600000000),(71,$p1,4,1600000000),(72,$p2,0,1600000000);
            INSERT INTO guild_roles(guild_id,grade,permissions,name) VALUES(71,4,32768,'Cargo role')
            """, masterId, peerId, foreignId);
        await fixture.StartAsync();
        using var master = Open(fixture);
        EnterResumed(master, fixture);
        using var peer = Open(fixture, "20001");
        EnterResumed(peer, fixture);
        // 先等新连接完成Join，再确认旧连接队列；跨连接通知不以另一连接的ACK作为完成条件。
        peer.Barrier(packet => SameGuildPresence(peer, packet, masterId));
        master.Barrier(packet => SameGuildPresence(master, packet, peerId));
        using var foreign = Open(fixture, "20002");
        var visible = EnterResumed(foreign, fixture);
        That(!visible.Contains(masterId) && !visible.Contains(peerId), "相同guildagit地图中另一个公会不收到先进入成员的Actor");
        master.Barrier(packet =>
        {
            That(!IsActor(packet, foreignId), "公会大厅人物隔离不会广播另一个公会新进入的角色");
            SameGuildPresence(master, packet, peerId);
        });
        peer.Barrier(packet =>
        {
            That(!IsActor(packet, foreignId), "同会观察者也只收到本公会人物");
            SameGuildPresence(peer, packet, masterId);
        });
        byte[] Deposit(int count) => Build(writer =>
        {
            writer.Write((byte)0); writer.Write((ushort)121); writer.Write(template.Id); writer.Write(count); writer.Write((ushort)0);
        });
        byte[] Withdraw(int count) => Build(writer =>
        {
            writer.Write((ushort)0); writer.Write(template.Id); writer.Write(count); writer.Write((byte)0);
        });
        master.Send(247, Deposit(5));
        master.Expect(1, 247, [0, 196, 0]);
        That(fixture.Query("SELECT count(*) FROM guild_cargo_receipts") is [[0L]], "未加载261不得提前存入，也不生成成功回执");
        master.Send(261);
        var initial = Decode(master.Expect(1, 261).Body);
        That(initial.Capacity == 8 && initial.Items.Length == 0, "真实261首次打开初始化8格并返回空快照");
        peer.Send(261);
        Decode(peer.Expect(1, 261).Body);
        master.Send(247, Deposit(5));
        master.Expect(1, 247, [1, 0, 121, 0, 5, 0, 0, 0, 0, 0]);
        var inventoryDelta = master.Expect(0, 14).Body;
        That(inventoryDelta[0] == 0, "247成功ACK先于真实Main库存差量");
        var stored = Decode(master.Expect(1, 261).Body);
        That(stored.Items is [{ Slot: 0, Count: 5 }] && stored.Items[0].ItemId == template.Id,
            "247完成后仓库快照使用95本版123字节物品行");
        var peerNotice = Decode(peer.Expect(0, 237).Body, false);
        That(peerNotice.Items is [{ Count: 5 }], "同会已加载仓库成员收到NOTI237权威同步");
        foreign.Barrier(packet => That(packet.Opcode != 237, "另一个公会不接收仓库资产通知"));
        byte[] Move(short from, short to) => Build(writer =>
        {
            writer.Write(from); writer.Write(template.Id); writer.Write(to); writer.Write(0);
        });
        string moveAssets = fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT item_uid,account,character_id,item_core FROM character_inventory_items ORDER BY item_uid");
        master.Send(249, Move(0, 1));
        master.Expect(1, 249, [1, 0, 0, 1, 0]);
        That(Decode(master.Expect(1, 261).Body).Items is [{ Slot: 1, Count: 5 }],
            "249确认后直接发布仓库权威快照，不伪造个人库存差量");
        That(Decode(peer.Expect(0, 237).Body, false).Items is [{ Slot: 1, Count: 5 }], "249移动同步给同会已打开观察者");
        peer.Send(249, Move(1, 0));
        peer.Expect(1, 249, [1, 1, 0, 0, 0]);
        That(Decode(peer.Expect(1, 261).Body).Items is [{ Slot: 0, Count: 5 }] &&
            Decode(master.Expect(0, 237).Body, false).Items is [{ Slot: 0, Count: 5 }], "另一账号可以按新版本移回原槽");
        That(fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT item_uid,account,character_id,item_core FROM character_inventory_items ORDER BY item_uid") == moveAssets &&
            fixture.Query("SELECT count(*) FROM guild_cargo_history") is [[1L]],
            "249往返保持中央UID、完整核心、角色版本及金币不变，不增加存取历史");
        peer.Send(248, Withdraw(5));
        peer.Expect(1, 248, [1, 0, 0, 0, 121, 0, 5, 0, 0, 0]);
        peer.Expect(0, 14);
        That(Decode(peer.Expect(1, 261).Body).Items.Length == 0, "248确认先于背包差量及空仓库刷新");
        That(Decode(master.Expect(0, 237).Body, false).Items.Length == 0, "另一成员取出后观察者仓库同步清空");
        That(fixture.Query("SELECT i.account,json_extract(i.item_core,'$.Value') FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid WHERE s.character_id=$p0 AND s.list_type=0 AND s.slot_index=121", peerId)
            is [["20001", 5L]] && fixture.Query("SELECT item_uid,json_extract(item_core,'$.Value') FROM character_inventory_items WHERE item_uid=$p0", originalUid)
            is [[long _, 15L]], "真实跨账号存取保持数量守恒，拆分原UID留在原账号");
        master.Send(262);
        byte[] history = master.Expect(1, 262).Body;
        That(history.Length == 5 + 2 * 57 && history[0] == 1 && U32(history, 1) == 2,
            "262历史返回两条完整57字节记录，存取各一条");
        master.Send(249, [0, 0, 1, 0]);
        master.Expect(1, 249, [0, 8, 0]);
        master.Send(261, [1]);
        master.Expect(1, 261, [0, 8, 0]);
        master.Barrier();
        GuildHallTcpTests.Run(fixture, master, peer, foreign, resources.Towns, hall, position, street, masterId, peerId);
        Console.WriteLine($"PASS {Count - before} 公会仓库真实TCP、同事务资产、包序、观察者与公会大厅隔离断言");
    }

    static bool IsActor(WirePacket packet, ushort id) => packet is { Kind: 0, Opcode: 2, Body.Length: > 122 } &&
        packet.Body[0] == 0 && U16(packet.Body, 1) == 1 && U16(packet.Body, 121) == id;

    static void SameGuildPresence(TcpPeer peer, WirePacket packet, ushort id)
    {
        // 入城尾部补发的公会被动属性（守护石1276）按真实账本单独核验，仍不接受其它通知。
        if (packet is { Kind: 0, Opcode: 487 })
        {
            peer.CheckGuildPassiveBuffs(packet);
            return;
        }
        That(IsActor(packet, id) ||
            packet is { Kind: 0, Opcode: 3, Body.Length: 4 } && packet.Body[0] == 1 &&
                U16(packet.Body, 1) == id && packet.Body[3] == 0 ||
            packet is { Kind: 0, Opcode: 22, Body.Length: 9 } && U16(packet.Body, 0) == id ||
            packet is { Kind: 0, Opcode: 23, Body.Length: 10 } && U16(packet.Body, 0) == id,
            "入厅屏障仅接收指定同会角色的Actor、状态、地区和位置，不跳过其他通知");
    }

    static HashSet<ushort> EnterResumed(TcpPeer peer, TcpTestFixture fixture)
    {
        // 正式原生换频道恢复路径保留已提交城镇；不通过GM移动或伪造Session准入。
        peer.Send(4, Build(writer => writer.Write((ushort)0)));
        var actors = new HashSet<ushort>();
        for (int guard = 0; guard < 256; guard++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 0, Opcode: 2, Body.Length: > 122 } && packet.Body[0] == 0 && U16(packet.Body, 1) == 1)
                actors.Add(U16(packet.Body, 121));
            if (packet is { Kind: 0, Opcode: 124 })
            {
                // 换频道恢复先124再绑定24，随后仍须完成场景技能、设置回放、任务和四类皮肤初始化：
                // 武器、伤害字体、技能插画与恒有原生默认20000的组队头像框；本夹具角色没有即时表情条目，服务端不发该类别。
                peer.Expect(0, 24);
                peer.Expect(0, 19);
                foreach (var ids in fixture.SkillGuideIds(0).Chunk(255))
                    peer.Expect(0, 1497, Build(writer =>
                    {
                        writer.Write(checked((byte)ids.Length));
                        foreach (ushort id in ids) { writer.Write(id); writer.Write((byte)5); }
                    }));
                // 19之后是角色设置回放：本夹具未保存账号设置与键位，服务端只补发非默认的390，173/454不出现（同9f6b45e4）。
                peer.Expect(0, 390);
                peer.Expect(0, 21);
                peer.Expect(0, 356);
                for (int skin = 0; skin < 4; skin++)
                {
                    peer.Expect(0, 1557);
                    peer.Expect(0, 1558);
                }
                peer.ReadWeeklyDungeon();
                return actors;
            }
        }
        throw new InvalidOperationException("Guild cargo TCP did not complete native town resume");
    }

    static (uint Capacity, (ushort Slot, int ItemId, int Count)[] Items) Decode(byte[] body, bool success = true)
    {
        using var reader = new BinaryReader(new MemoryStream(body));
        if (success) That(reader.ReadByte() == 1, "261原生成功标记");
        uint capacity = reader.ReadUInt32();
        int count = reader.ReadUInt16();
        That(count <= capacity && capacity is >= 8 and <= 56 && reader.BaseStream.Length - reader.BaseStream.Position == count * 123,
            "独立解码器核对容量、行数和95固定物品宽度");
        var items = new (ushort Slot, int ItemId, int Count)[count];
        for (int i = 0; i < count; i++)
        {
            ushort slot = reader.ReadUInt16(); int id = reader.ReadInt32(); int amount = reader.ReadInt32();
            reader.ReadBytes(113);
            items[i] = (slot, id, amount);
        }
        return (capacity, items);
    }
}
