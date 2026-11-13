using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Marriage;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Marriage;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class MarriageHomeTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        var clock = new Clock();
        var catalog = MarriageCatalog.Load(resources.Archive);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "marriage-home"), resources: resources, clock: clock);
        await fixture.StartAsync();
        using var first = Open(fixture, "homea"); using var second = Open(fixture, "homeb");
        first.Create("HomeA", 0); second.Create("HomeB", 1);
        fixture.Sql("UPDATE characters SET level=30,gold=10000000 WHERE id IN ($p0,$p1)", first.Id, second.Id);
        var repository = new CharacterRepository(fixture.Database);
        var furniture = catalog.Furniture.Values.First(row => !row.Placements.IsEmpty);
        ushort anchor = checked((ushort)furniture.Placements[0].PositionId);
        var seed = catalog.Seeds.First(); var tree = catalog.Trees[seed.Value];
        int water = tree.GrowPoints.OrderBy(row => row.Value).First().Key;
        var extraSeeds = catalog.Seeds.Where(row => row.Key != seed.Key)
            .Select((row, index) => (Seed: row.Key, Type: row.Value, Slot: checked((ushort)(70 + index)))).ToArray();
        long savedExpiry = clock.GetUtcNow().AddDays(tree.ExpireDays).ToUnixTimeSeconds();
        var owners = new AccountOwners();
        using (var ownerA = owners.Acquire("homea", () => { }))
        using (var ownerB = owners.Acquire("homeb", () => { }))
        {
            var a = repository.LoadCharacter(ownerA, 0); var b = repository.LoadCharacter(ownerB, 0);
            a = repository.Save(ownerA, a, resources.Towns.Room, resources.Towns.InitialReturn, new(a.Inventory, a.Inventory with
            {
                Items = a.Inventory.Items.AddRange(new InventoryItem[]
                {
                    new(0, new(InventoryListType.Main, 65), new(seed.Key, ItemKind.Consumable, 2)),
                    new(0, new(InventoryListType.Main, 66), new(water, ItemKind.Consumable, 5)),
                    new(0, new(InventoryListType.Main, 67), new(10002915, ItemKind.Consumable, 3)),
                    new(0, new(InventoryListType.Main, 68), new(catalog.UpgradeRingTickets[1].ItemId, ItemKind.Consumable, 1)),
                    new(0, new(InventoryListType.Main, 69), new(10002901, ItemKind.Consumable, 1))
                }).AddRange(extraSeeds.Select(row => new InventoryItem(0,
                    new(InventoryListType.Main, checked((short)row.Slot)), new(row.Seed, ItemKind.Consumable, 1))))
            }, "marriage_fixture"));
            b = repository.Save(ownerB, b, resources.Towns.Room, resources.Towns.InitialReturn);
            var record = MarriageWeddingRules.CreateEngagement(Guid.NewGuid(), 701, a.Id, b.Id, clock.GetUtcNow().ToUnixTimeSeconds()) with
            {
                State = 4, MarriedAt = clock.GetUtcNow().ToUnixTimeSeconds(),
                Tree = new() { Seed = seed.Key, Expiry = savedExpiry, WaterDay = QuestCycle.Day(clock.GetUtcNow()) },
                Storage = ImmutableDictionary<ushort, MarriageFurnitureStack>.Empty.Add(0, new(furniture.ItemId, 2))
            };
            record = record with { Members = record.Members
                .SetItem(a.Id, new() { CapsuleGauge = catalog.GrowthCapsule.MaxGage, CapsulesOwned = 1, LovePoints = 500 })
                .SetItem(b.Id, new() { CapsuleDay = QuestCycle.Day(clock.GetUtcNow()), CapsulesTaken = catalog.GrowthCapsule.MaxTakenCount }) };
            repository.CommitMarriageTogether(ownerA, ownerB, a, b, new(a.Inventory, a.Inventory, "marriage_fixture"),
                new(b.Inventory, b.Inventory, "marriage_fixture"), null, record);
        }
        first.Select(); Flush(first);
        first.Send(1055, []); Ack(Flush(first), 1055, 0);
        That(repository.FindMarriage(first.Id)!.Members[second.Id].CapsulesPending == 1,
            "配偶未选角时的赠送同样保存待领，不依赖接收方在线或先打开窗口");
        second.Select(); Flush(first); Flush(second);
        That(second.MarriageCapsules[5] == 1,
            "离线获赠后登录恢复一份可领取礼物，符合原生待领大于零才发送1056的前置条件");
        second.Send(1056, []); Ack(Flush(second), 1056, -7); Flush(first);
        That(repository.FindMarriage(first.Id)!.Members[second.Id].CapsulesPending == 1,
            "待领不等于今日可领取额度，额度耗尽仍由服务端拒绝并保留礼物");
        foreach (var peer in new[] { first, second })
            That(U32(peer.MarriageRoom, peer.MarriageRoom.Length - 13) == seed.Value &&
                U32(peer.MarriageRoom, peer.MarriageRoom.Length - 9) == savedExpiry,
                "登录保留旧树存档并映射原生树index，不下发种子物品编号");
        foreach (var peer in new[] { first, second })
        {
            var room = resources.Towns.Room;
            peer.Send(36, Area(101, 0, 300, 200, room.Town, room.Area));
            var packets = Flush(peer);
            That(packets.Any(p => p.Opcode == 23) && packets.Any(p => p.Opcode == 1011) && packets.Any(p => p.Opcode == 1012),
                "正式婚房入场回放家具、树和共用背包");
            var roomBody = packets.Last(p => p.Opcode == 1011).Body;
            That(U32(roomBody, roomBody.Length - 13) == seed.Value && U32(roomBody, roomBody.Length - 9) == savedExpiry,
                "已有夫妻树的婚房入场发送配置index及原期限，覆盖种植后重新进房崩溃路径");
        }
        Flush(first); Flush(second);
        first.Send(1053, []);
        var removedTree = Flush(first); Ack(removedTree, 1053, 0); Flush(second);
        That(removedTree.Single(p => p.Opcode == 1015).Body.SequenceEqual(new byte[13]), "移除后原生树状态完整清空");
        first.Send(1048, Structured(Build(w => { Slot(w, 1, 0, furniture.ItemId); Slot(w, 0, anchor, furniture.ItemId); })));
        Ack(Flush(first), 1048, -1);
        That(repository.FindMarriage(first.Id) is { Furniture.IsEmpty: true } unopened && unopened.Storage[0].Count == 2,
            "尚未打开编辑窗口的请求不误报爱人正在占用，也不修改家具");
        first.Send(1047, Structured([0])); Ack(Flush(first), 1047, 0); Flush(second);
        second.Send(1047, Structured([0])); Ack(Flush(second), 1047, -4);
        string furnitureBeforePlacement = fixture.Snapshot("SELECT aggregate_json FROM marriage_records");
        second.Send(1048, Structured(Build(w => { Slot(w, 1, 0, furniture.ItemId); Slot(w, 0, anchor, furniture.ItemId); })));
        Ack(Flush(second), 1048, -20);
        ushort invalidAnchor = checked((ushort)catalog.RoomPositions.Keys.First(position =>
            !furniture.Placements.Any(row => row.PositionId == position)));
        first.Send(1048, Structured(Build(w => { Slot(w, 1, 0, furniture.ItemId); Slot(w, 0, invalidAnchor, furniture.ItemId); })));
        Ack(Flush(first), 1048, -22);
        That(fixture.Snapshot("SELECT aggregate_json FROM marriage_records") == furnitureBeforePlacement,
            "未持编辑锁与不合法摆放位置分别返回原生-20/-22，不误报伴侣丢失或修改家具");
        first.Send(1048, Structured(Build(w => { Slot(w, 1, 0, furniture.ItemId); Slot(w, 0, anchor, furniture.ItemId); })));
        Ack(Flush(first), 1048, 0);
        var peerFurniture = Flush(second);
        uint expectedScore = checked((uint)resources.ItemResources.Items.Get(furniture.ItemId).ShopPrice.Materials.Sum(row => (long)row.Count));
        That(peerFurniture.Any(p => p is { Kind: 0, Opcode: 1016 } && p.Body.SequenceEqual(Build(w =>
            { w.Write((uint)first.Id); w.Write((uint)second.Id); w.Write((byte)4); w.Write((byte)0); w.Write(expectedScore); }))),
            "家具摆放后配偶收到远端属性1016，逐字段匹配已提交身份、婚戒与资源材料装饰分");
        That(repository.FindMarriage(first.Id) is { } placed && placed.Furniture[anchor].ItemId == furniture.ItemId && placed.Storage[0].Count == 1,
            "编辑锁持有者摆放一件，持久共享背包只少一件");
        first.Send(1048, Structured(Build(w => { Slot(w, 0, anchor, furniture.ItemId); Slot(w, 1, 3, furniture.ItemId); })));
        Ack(Flush(first), 1048, 0); Flush(second);
        That(repository.FindMarriage(first.Id) is { Furniture.IsEmpty: true } recalled &&
            recalled.Storage[0].Count == 1 && recalled.Storage[3].Count == 1,
            "原生双端同编号收回至空保管槽，数据库数量守恒");
        first.Send(1048, Structured(Build(w => { Slot(w, 1, 3, furniture.ItemId); Slot(w, 0, anchor, furniture.ItemId); })));
        Ack(Flush(first), 1048, 0); Flush(second);
        string furnitureBeforeReplay = fixture.Snapshot("SELECT aggregate_json FROM marriage_records");
        first.Send(1048, Structured(Build(w => { Slot(w, 1, 3, furniture.ItemId); Slot(w, 0, anchor, furniture.ItemId); })));
        Ack(Flush(first), 1048, -1);
        That(fixture.Snapshot("SELECT aggregate_json FROM marriage_records") == furnitureBeforeReplay,
            "已耗尽来源的摆放重放返回原生请求错误，不误报伴侣丢失或推进关系版本");
        first.Send(1047, Structured([1])); Ack(Flush(first), 1047, 0);
        second.Send(1047, Structured([0])); Ack(Flush(second), 1047, 0);
        second.Send(1048, Structured(Build(w => { Slot(w, 0, anchor, furniture.ItemId); Slot(w, 1, 0, furniture.ItemId); })));
        Ack(Flush(second), 1048, 0); Flush(first);
        That(repository.FindMarriage(first.Id) is { Furniture.IsEmpty: true } returned && returned.Storage[0].Count == 2,
            "配偶取得锁后收回家具并合并数量");
        string beforeInvalidRing = fixture.Snapshot("SELECT aggregate_json FROM marriage_records");
        first.Send(1038, Structured(Build(w => { w.Write(68u); w.Write((byte)2); })));
        var invalidRing = Flush(first).Single(p => p.Kind == 1 && p.Opcode == 1038).Body;
        Bytes(invalidRing, [1, 0xfe, 0xff, 0xff, 0xff, 0], "婚戒越级走原生无法升级分支，失败仍携带必读等级尾字节");
        That(fixture.Snapshot("SELECT aggregate_json FROM marriage_records") == beforeInvalidRing, "婚戒拒绝不扣券或改关系版本");
        first.Send(1038, Structured(Build(w => { w.Write(68u); w.Write((byte)1); })));
        Ack(Flush(first), 1038, 0); Flush(second);
        That(repository.FindMarriage(first.Id)!.RingLevel == 1 && fixture.Query("SELECT gold FROM characters WHERE id=$p0", first.Id) is [[10000000L]],
            "正式升级戒指只扣现有升级券，不重复扣商店售价");
        foreach (var (peer, job) in new[] { (first, (byte)0), (second, (byte)1) })
        {
            int weapon = resources.Characters.Get(job).Equipment.Single(row => row.Slot == 12).Template.Id;
            for (int i = 0; i < 6; i++)
            {
                bool takeOff = i % 2 == 0;
                peer.Send(19, Build(w =>
                {
                    w.Write(takeOff ? (byte)3 : (byte)0); w.Write(takeOff ? (short)12 : (short)9);
                    w.Write(weapon); w.Write(0);
                    w.Write(takeOff ? (byte)0 : (byte)3); w.Write(takeOff ? (short)9 : (short)12);
                    w.Write(0); w.Write(0); w.Write(-1); w.Write((ushort)0);
                }));
                var refreshed = Flush(peer);
                That(refreshed.Single(packet => packet is { Kind: 1, Opcode: 19 }).Body[0] == 1,
                    "婚房内普通装备反复脱穿走真实库存提交");
                var actor = refreshed.Single(packet => packet is { Kind: 0, Opcode: 2 } && packet.Body[0] == 0 &&
                    packet.Body.Length >= 123 && U16(packet.Body, 121) == peer.Id).Body;
                That(actor[28] == 4 && actor[29] == 1 && refreshed.Any(packet => packet is { Kind: 0, Opcode: 2 } && packet.Body[0] == 1),
                    "连续Actor/Player刷新仍携带本人的已婚状态及当前婚戒，包含双方各六次脱穿");
                That(!refreshed.Any(packet => packet.Opcode == 1003),
                    "普通外观刷新直接保留原生字段，不补发会清理伴侣预览的婚姻初始化");
            }
            Flush(first); Flush(second);
        }
        int loveBefore = repository.FindMarriage(first.Id)!.Members[first.Id].LovePoints;
        first.Send(1039, Structured(BitConverter.GetBytes(69u))); Ack(Flush(first), 1039, 0); Flush(second);
        That(repository.FindMarriage(first.Id)!.Members[first.Id].LovePoints == loveBefore + 1,
            "心意点道具使用后保存实际增量并回放双方关系");
        int shopId = resources.ItemResources.Shops.Shops.Single(row => row.Value.NpcId == 20011 && row.Value.Items.Contains(10002797)).Key;
        first.Send(21, Build(w => { w.Write(10002797); w.Write(1); w.Write(shopId); w.Write(20011); }));
        var bought = Flush(first); Flush(second);
        That(bought.Any(p => p is { Kind: 1, Opcode: 21 } && p.Body[0] == 1) &&
            repository.FindMarriage(first.Id) is { } purchased && purchased.Storage[1].ItemId == 10002797 &&
            purchased.Members[first.Id].LovePoints == loveBefore + 1 - 100 &&
            fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=10002797", first.Id) is [[0L]],
            "正式心意点商店购买直接入夫妻共享仓库，扣一百点且不创建主背包家具");
        first.Send(1050, Structured(Build(w => { w.Write((ushort)65); w.Write(seed.Key); })));
        var plantedTree = Flush(first); Ack(plantedTree, 1050, 0);
        var partnerTree = Flush(second);
        That(U32(plantedTree.Single(p => p.Opcode == 1015).Body, 0) == seed.Value &&
            partnerTree.Single(p => p.Opcode == 1015).Body.SequenceEqual(plantedTree.Single(p => p.Opcode == 1015).Body),
            "种植成功后双方收到相同原生树index，不泄漏服务端种子存档语义");
        first.Send(1052, []); Ack(Flush(first), 1052, -6);
        That(repository.FindMarriage(first.Id)!.Tree is { Growth: 0 }, "未成熟收获提示成长不足，不清树或伪造无树");
        while (repository.FindMarriage(first.Id)!.Tree!.Growth < tree.Steps[^1].GrowPoint)
        {
            first.Send(1051, Structured(Build(w => { w.Write((ushort)66); w.Write(water); })));
            Ack(Flush(first), 1051, 0); Flush(second);
            var currentTree = repository.FindMarriage(first.Id)!.Tree!;
            if (currentTree.Waters == tree.DayWaterCount && currentTree.Growth < tree.Steps[^1].GrowPoint)
            {
                string beforeWater = fixture.Snapshot("SELECT item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", first.Id);
                first.Send(1051, Structured(Build(w => { w.Write((ushort)66); w.Write(water); })));
                Ack(Flush(first), 1051, -6);
                That(fixture.Snapshot("SELECT item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", first.Id) == beforeWater,
                    "每日次数耗尽不消耗洒水壶，跨日后仍可继续成长");
                clock.Advance(TimeSpan.FromDays(1));
                var daily = Flush(first);
                That(daily.Any(p => p is { Opcode: 1015, Body.Length: 13 } && p.Body[12] == 0),
                    "持续在线跨日后，普通请求即刷新已耗尽的浇水UI，不必重登或重进婚房");
                var partnerDaily = Flush(second);
                That(partnerDaily.Any(p => p is { Opcode: 1018, Body.Length: 7 } && p.Body[5] == 1),
                    "胶囊跨日通知保留一份待领礼物，不把每日次数清零误投影为没有礼物");
            }
        }
        first.Send(1052, []);
        var harvest = Flush(first); Ack(harvest, 1052, 0); Flush(second);
        That(harvest.Any(p => p.Opcode == 1017) && repository.FindMarriage(first.Id)!.Tree is null,
            "种植浇水到成熟后领取实体奖励并清树");
        first.Send(1052, []); Ack(Flush(first), 1052, -4);
        foreach (var extra in extraSeeds)
        {
            first.Send(1050, Structured(Build(w => { w.Write(extra.Slot); w.Write(extra.Seed); })));
            var planted = Flush(first); Ack(planted, 1050, 0); Flush(second);
            That(U32(planted.Single(p => p.Opcode == 1015).Body, 0) == extra.Type,
                "每种PVF种子均映射对应树index " + extra.Seed);
            first.Send(1053, []); Ack(Flush(first), 1053, 0); Flush(second);
        }
        first.Send(1054, []); Ack(Flush(first), 1054, 0); Flush(second);
        first.Send(1054, []); Ack(Flush(first), 1054, -4);
        second.Send(1055, []); Ack(Flush(second), 1055, -4);
        first.Send(1055, []); Ack(Flush(first), 1055, 0);
        var giftNotice = Flush(second).Single(p => p is { Kind: 0, Opcode: 1018 });
        Bytes(giftNotice.Body, new byte[] { 0, 0, 0, 0, 0, 2, 1 },
            "在线赠送通知带累计两份待领和新礼物标记，客户端具备真实领取前置条件");
        second.Send(1056, []);
        var firstTake = Flush(second); Ack(firstTake, 1056, 0); Flush(first);
        Bytes(firstTake.Single(p => p is { Kind: 1, Opcode: 1056 }).Body, new byte[] { 1, 0, 0, 0, 0, 1 },
            "首次领取后回执保留一份待领，允许客户端再次领取");
        second.Send(1056, []);
        var lastTake = Flush(second); Ack(lastTake, 1056, 0); Flush(first);
        Bytes(lastTake.Single(p => p is { Kind: 1, Opcode: 1056 }).Body, new byte[] { 1, 0, 0, 0, 0, 0 },
            "最后一份领取后回执清空待领，不把当天已领两次变成仍有两个礼物");
        That(repository.FindMarriage(first.Id) is { } capsules && capsules.Members[first.Id].CapsulesOwned == 0 &&
            capsules.Members[second.Id] is { CapsulesPending: 0, CapsulesTaken: 2 } &&
            fixture.Query("SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1", second.Id, catalog.GrowthCapsule.CapsuleId) is [[2L]],
            "离线及在线赠送后连续领取原子转移两份真实胶囊，持久已领与待领分别计数");
        second.Send(1056, []); That(unchecked((int)U32(Flush(second).Single(p => p.Kind == 1 && p.Opcode == 1056).Body, 1)) < 0,
            "重复领取不再产生胶囊");
        foreach (var peer in new[] { first, second })
        {
            var room = resources.Towns.Room;
            peer.Send(36, Area(room.Town, room.Area, 300, 200, 101, 0));
            That(Flush(peer).Any(p => p.Opcode == 23), "婚房负101出口返回入场房间");
        }
        Flush(first); Flush(second);
        var street = resources.Towns.ResolveRoomReturn(resources.Towns.Room, resources.Towns.InitialReturn, 30, new HashSet<int>());
        second.Travel(street.Town, street.Area, street.X, street.Y, resources.Towns.Room.Town, resources.Towns.Room.Area);
        Flush(first); Flush(second);
        first.Send(1045, Structured(Build(w => { w.Write(701u); w.Write(67u); })));
        Ack(Flush(first), 1045, 0);
        var moved = Enumerable.Range(1, 30).Select(delta => street with { X = checked((ushort)(street.X + delta)) })
            .First(point => resources.Towns.IsPosition(point));
        second.Send(35, Move(moved.X, moved.Y)); Flush(second);
        first.Send(1046, Structured(Build(w => { w.Write(67u); w.Write((int)street.Town); w.Write((int)street.Area); w.Write((int)street.X); w.Write((int)street.Y); })));
        Bytes(Flush(first).Single(p => p.Kind == 1 && p.Opcode == 1046).Body, [0],
            "原生空1046回调的失败只拒绝，不伪装01成功或追加未读取的错误结构");
        That(fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=10002915", first.Id) is [[3L]],
            "报价后伴侣移动拒绝旧确认，不提前扣传送券");
        second.Send(35, Move(street.X, street.Y)); Flush(second);
        first.Send(1045, Structured(Build(w => { w.Write(701u); w.Write(67u); })));
        Ack(Flush(first), 1045, 0);
        first.Send(1046, Structured(Build(w => { w.Write(67u); w.Write((int)street.Town); w.Write((int)street.Area); w.Write((int)street.X); w.Write((int)street.Y); })));
        var teleport = Flush(first);
        That(teleport.Any(p => p.Kind == 1 && p.Opcode == 1046 && p.Body.SequenceEqual(new byte[] { 1 })) && teleport.Any(p => p.Opcode == 23),
            "伴侣传送确认成功并发布正式旅行");
        Flush(second);
        That(fixture.Query("SELECT json_extract(i.item_core,'$.Value') FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid WHERE i.character_id=$p0 AND s.slot_index=67", first.Id) is [[2L]],
            "传送只扣一张已有券，不另扣商店五枚兑换材料");
        foreach (var peer in new[] { first, second })
        {
            Flush(peer);
            peer.Travel(resources.Towns.Room.Town, resources.Towns.Room.Area, 300, 200, street.Town, street.Area, returnTown: street.Town);
            peer.Send(36, Area(101, 0, 300, 200, resources.Towns.Room.Town, resources.Towns.Room.Area)); Flush(peer);
        }
        Flush(first); Flush(second);
        first.Send(1034, Structured(Build(w => { w.Write((uint)second.Id); w.Write((byte)6); w.Write((byte)0); w.Write((byte)0); })));
        var endA = Flush(first); var endB = Flush(second);
        That(endA.Any(p => p.Opcode == 1005) && endB.Any(p => p.Opcode == 1005) && endA.Any(p => p.Opcode == 23) && endB.Any(p => p.Opcode == 23) &&
            repository.FindMarriage(first.Id) is null && fixture.Query("SELECT count(*) FROM characters WHERE id IN ($p0,$p1) AND town=101", first.Id, second.Id) is [[0L]],
            "强制离婚清除关系及婚房权益，在线双方正式离场");
        That(fixture.Query("PRAGMA foreign_key_check").Length == 0, "婚房后续链保持隔离库外键完整");
        Console.WriteLine($"PASS {Count - checks} marriage home TCP assertions");
    }

    static byte[] Structured(byte[] body) => [.. new byte[13], .. body];
    static void Slot(BinaryWriter writer, byte container, ushort slot, int item) { writer.Write(container); writer.Write(slot); writer.Write(item); }
    static void Ack(List<WirePacket> packets, ushort opcode, int result) => Bytes(packets.Single(p => p.Kind == 1 && p.Opcode == opcode).Body.AsSpan(0, 5),
        Build(w => { w.Write((byte)1); w.Write(result); }), "婚房请求回执逐字段匹配");
    static List<WirePacket> Flush(TcpPeer peer)
    {
        peer.SendBarrier(); List<WirePacket> packets = [];
        for (int i = 0; i < 100; i++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 1, Opcode: 1347 })
            {
                Bytes(packet.Body, [1], "婚房请求屏障成功");
                peer.CheckMarriagePartnerRestores(packets);
                return packets;
            }
            if (packet is { Kind: 0, Opcode: 411 })
            {
                peer.ReadChatEmoticons(packet);
                packets.Add(packet);
                continue;
            }
            // 假时钟跨天后的首个请求会让 ServiceDayProjection.PublishAsync 跑完整轮服务日刷新，这些都是全局日切通知，不是婚房串包：
            // 36 FATIGUE、14 UPDATE_ITEM_LIST、21 ACCEPTABLE_QUEST_LIST、356 CLEAR_QUEST_LIST、574 ACCEPTED_QUEST 来自 QuestCycleAsync；
            // 550 DUNGEON_ENTER_COUNT_INFO、1343 ADVENTURE_INFO、1349 ADVENTURE_LV_EXP 来自 LegacyServiceDayAsync；
            // 1536 ASSAULT_MODE_DAILY_INFO 来自 AssaultServiceDayAsync。720 WEEKLY_DUNGEON_INFO_RESETDAILY 同为日切通知。
            // 832/833为深渊日切状态；普通装备穿脱以1681静默同步变化槽，仍精确检查Actor/Player中的婚姻字段。
            That(packet.Kind == 0 && packet.Opcode is 2 or 3 or 4 or 6 or 13 or 14 or 21 or 22 or 23 or 24 or 36 or 356 or 541 or 550 or 574 or 720 or 832 or 833 or 1003 or 1005 or 1011 or 1012 or 1014 or 1015 or 1016 or 1017 or 1018 or 1343 or 1349 or 1379 or 1536 or 1681 ||
                packet.Kind == 1 && (packet.Opcode is >= 1034 and <= 1056 or 19 or 21), $"婚房只接收所属协议及场景通知，实际{packet.Kind}/{packet.Opcode}");
            packets.Add(packet);
        }
        throw new InvalidDataException("Marriage home notification budget exceeded");
    }
    sealed class Clock : TimeProvider
    {
        long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref seconds));
        public void Advance(TimeSpan value) => Interlocked.Add(ref seconds, checked((long)value.TotalSeconds));
    }
}
