using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class TeleportTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new Clock();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "teleport"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        var variants = new List<(Character Character, int Aura, byte Type, int Seconds)>();
        Character npcRole;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("Teleport", resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, null);
            var items = resources.ItemResources.Items;
            repository.Create(owner, candidate with { Level = 60, CompletedQuests = [3156], Inventory = candidate.Inventory with
            {
                Items = candidate.Inventory.Items.Add(InventoryRewardGrantService.CreateItem(items.Get(2600014), new(InventoryListType.Main, 65),
                    InventoryRewardGrantService.Create(items.Get(2600014), 3)))
                    .Add(InventoryRewardGrantService.CreateItem(items.Get(101590011), new(InventoryListType.Equipment, 9),
                        InventoryRewardGrantService.Create(items.Get(101590011))))
            } });
            foreach (var row in new[] { ("Upgrade600", 101590301, (byte)3, 600), ("Upgrade300", 401590036, (byte)3, 300),
                ("PartyAura", 101590229, (byte)0, 600), ("NoAura", 101590218, (byte)1, 0) })
            {
                var role = repository.Create(owner, candidate with { Name = row.Item1, Level = 60, CompletedQuests = [3156],
                    Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.Add(InventoryRewardGrantService.CreateItem(
                        items.Get(row.Item2), new(InventoryListType.Equipment, 9), InventoryRewardGrantService.Create(items.Get(row.Item2)))) } });
                variants.Add((role, row.Item2, row.Item3, row.Item4));
            }
            npcRole = repository.Create(owner, candidate with { Name = "NpcLanding", Level = 60, CompletedQuests = [3156],
                Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.Add(InventoryRewardGrantService.CreateItem(
                    items.Get(101590011), new(InventoryListType.Equipment, 9), InventoryRewardGrantService.Create(items.Get(101590011)))) } });
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Send(434, Party(38, 0, 1327, 234, 1)); peer.Expect(1, 434, [0, 19, 0, 38, 0, 0, 0, 0, 1]);
            peer.Send(237, Potion(65, 2600014, 38, 0, 1327, 234)); peer.Expect(1, 237, [0, 23, 0]);
            peer.Send(692, Name("Missing")); peer.Expect(1, 692, [1]);
            peer.Select(prepared: true);
            peer.Send(469, Name("Missing")); peer.Expect(1, 469, [0, 1, 0]); peer.Barrier();
            peer.Send(237, Potion(65, 2600014, 38, 0, 1327, 234));
            peer.Expect(1, 237, [1, 65, 0, 78, 172, 39, 0]);
            peer.Expect(0, 1681); peer.Expect(0, 1681); peer.Expect(0, 23); peer.Expect(0, 24); peer.Barrier();
            That(Count() == 2 && fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", peer.Id) is [[38L, 0L, 1327L, 234L]],
                "真实TCP药剂ACK、绝对库存及23/24后保存目的地，无额外124");
            peer.Send(237, Potion(65, 2600014, 38, 0, 1327, 234)); peer.Expect(1, 237, [0, 23, 0]); peer.Barrier();
            That(Count() == 2, "原药请求重放仅拒绝，不重复扣件");
            using (var observer = Open(fixture, "departure-observer"))
            {
                observer.Create("Observer"); observer.Select();
                observer.Send(1426); observer.Expect(0, 23); observer.Expect(0, 24);
                observer.Batch((0, 2), (0, 3), (0, 23), (0, 22)); observer.Barrier();
                peer.Batch((0, 2), (0, 3), (0, 23), (0, 22)); peer.Barrier();
                peer.Send(470, Solo(-1, 7, 39, 3, 783, 205)); peer.Expect(1, 470, [1]);
                peer.Expect(0, 379, Effect(1, peer.Id)); peer.Expect(0, 6, Build(w => w.Write(observer.Id)));
                peer.Expect(0, 23); peer.Expect(0, 24); peer.Expect(0, 379, Effect(2, peer.Id));
                peer.Expect(0, 378, [1, 2, 164, 1, 0, 0]); peer.Barrier();
                observer.Expect(0, 379, Effect(1, peer.Id)); observer.Expect(0, 6, Build(w => w.Write(peer.Id))); observer.Barrier();
                observer.Send(7); observer.Expect(1, 7, [1]); observer.Expect(0, 2);
            }
            That(fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", peer.Id) is [[39L, 3L, 783L, 205L]], "真实NPC身份与任务准入后光环跨图落库");
            peer.Send(470, Solo(-1, 7, 39, 3, 783, 205)); peer.Expect(1, 470, [0, 1, 0, 0, 0, 0, 0]); peer.Barrier();
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(prepared: true, inventoryNotices: [(0, 378)]); peer.Barrier();
            peer.Send(470, Solo(-1, 7, 39, 3, 783, 205)); peer.Expect(1, 470, [0, 1, 0, 0, 0, 0, 0]); peer.Barrier();
            That(Count() == 2 && fixture.Query("SELECT count(*) FROM character_teleport_cooldowns") is [[1L]],
                "重选恢复冷却；失败ACK之后后续请求可操作且没有额外库存变化");
            clock.Offset = TimeSpan.FromSeconds(420);
            using var target = Open(fixture, "beta"); target.Create("Target"); target.Select();
            target.Send(1426); target.Expect(0, 23); target.Expect(0, 24); target.Barrier();
            peer.Send(469, Name("Target"));
            var precheck = peer.Expect(1, 469).Body;
            That(precheck[0] == 1 && U32(precheck, 2) == target.Id && U32(precheck, 6) == 6 &&
                precheck.AsSpan(10, 6).SequenceEqual(System.Text.Encoding.ASCII.GetBytes("Target")) &&
                precheck.AsSpan(16, 3).SequenceEqual(new byte[] { 1, 0, 0 }),
                "玩家预检查只查询当前公开城镇成员，不创建离线目标");
            peer.Send(470, Solo(target.Id, -1, 255, 255, 0, 0)); peer.Expect(1, 470, [1]); peer.Expect(0, 379, Effect(1, peer.Id));
            peer.Expect(0, 23); peer.Expect(0, 24); peer.Expect(0, 2); peer.Expect(0, 3); peer.Expect(0, 23); peer.Expect(0, 22);
            peer.Expect(0, 379, Effect(2, peer.Id)); peer.Expect(0, 378, [1, 2, 164, 1, 0, 0]); peer.Barrier();
            target.Expect(0, 2); target.Expect(0, 3); target.Expect(0, 23); target.Expect(0, 22);
            target.Expect(0, 379, Effect(2, peer.Id)); target.Barrier();
            That(fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", peer.Id) is [[38L, 0L, 622L, 196L]],
                "玩家传送取实际目标位置，伪造客户端目的地不进入保存；同屏双方收到真实角色");
        }
        foreach (var row in variants)
        {
            using var peer = Open(fixture);
            peer.Select(row.Character.Slot, prepared: true); peer.Barrier();
            var before = fixture.Query("SELECT revision,town,area,x,y FROM characters WHERE id=$p0", peer.Id)[0];
            peer.Send(434, Party(38, 0, 1327, 234, 1)); peer.Expect(1, 434, [0, 19, 0, 38, 0, 0, 0, 0, 1]); peer.Barrier();
            That(before.SequenceEqual(fixture.Query("SELECT revision,town,area,x,y FROM characters WHERE id=$p0", peer.Id)[0]) &&
                fixture.Query("SELECT count(*) FROM character_teleport_cooldowns WHERE character_id=$p0", peer.Id) is [[0L]],
                "没有队伍上下文的434明确失败并解除原生等待，不移动队长、不写位置或冷却");
            peer.Send(470, Solo(-1, -1, 38, 0, 1327, 234));
            if (row.Type is 0 or 1)
            {
                peer.Expect(1, 470, [0, 1, 0, 0, 0, 0, 0]); peer.Barrier();
                That(fixture.Query("SELECT count(*) FROM character_teleport_cooldowns WHERE character_id=$p0", peer.Id) is [[0L]],
                    "真实组队或无能力光环不能借470取得个人传送资格");
                if (row.Type == 0) await SingleMemberPartyAsync(peer, row.Character.Slot);
                continue;
            }
            peer.Expect(1, 470, [1]);
            peer.Expect(0, 379, Effect(1, peer.Id)); peer.Expect(0, 23); peer.Expect(0, 24);
            peer.Expect(0, 379, Effect(2, peer.Id)); peer.Expect(0, 378, Cooldown(row.Type, row.Seconds)); peer.Barrier();
            That(fixture.Query("SELECT potion_until,aura_until,upgrade_aura_until FROM character_teleport_cooldowns WHERE character_id=$p0", peer.Id)
                is [[0L, 0L, long until]] && until == clock.GetUtcNow().ToUnixTimeMilliseconds() + row.Seconds * 1000L &&
                fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", peer.Id) is [[38L, 0L, 1327L, 234L]],
                "真实高级光环" + row.Aura + "沿470切图，按资源300或600秒写入type3而不覆盖普通type2");
            peer.Send(470, Solo(-1, -1, 38, 0, 1327, 234)); peer.Expect(1, 470, [0, 1, 0, 0, 0, 0, 0]); peer.Barrier();
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            var entry = peer.Select(row.Character.Slot, prepared: true, inventoryNotices: [(0, 378)]);
            Bytes(entry.Single(packet => packet is { Kind: 0, Opcode: 378 }).Body, Cooldown(row.Type, row.Seconds),
                "重选完整资产初始化后恢复type3和真实剩余秒，不把高级冷却误投影为type2");
            peer.Send(469, Name("Target")); peer.Expect(1, 469, [0, 1, 0]); peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(npcRole.Slot, prepared: true); peer.Barrier();
            // 两份实收NPC请求；NPC导航固定点不属于普通步行矩形，不能用普通点选路径代替覆盖。
            foreach (string body in new[] { "FFFFFFFF9000000028006F07500005", "FFFFFFFF1200000027006504AF0005" })
            {
                byte[] request = Convert.FromHexString(body);
                byte town = request[8], area = request[9];
                ushort x = U16(request, 10), y = U16(request, 12);
                That(!resources.Towns.IsPosition(new(town, area, x, y), new HashSet<int> { 3156 }),
                    "实机NPC144/18落点确实在当前地图步行矩形外，原服务端校验会误拒绝");
                var before = fixture.Query("SELECT revision,town,area,x,y FROM characters WHERE id=$p0", peer.Id)[0];
                peer.Send(470, Solo(-1, -1, town, area, x, y)); peer.Expect(1, 470, [0, 1, 0, 0, 0, 0, 0]); peer.Barrier();
                That(before.SequenceEqual(fixture.Query("SELECT revision,town,area,x,y FROM characters WHERE id=$p0", peer.Id)[0]),
                    "相同坐标的无NPC地图点选仍拒绝，不提前修改位置、冷却或revision");
                peer.Send(470, request); peer.Expect(1, 470, [1]); peer.Expect(0, 379, Effect(1, peer.Id));
                var arrival = peer.Expect(0, 23).Body;
                var scene = peer.Expect(0, 24).Body;
                That(U16(arrival, 0) == peer.Id && arrival.AsSpan(2, 6).SequenceEqual(request.AsSpan(8, 6)) &&
                    scene.AsSpan(0, 2).SequenceEqual(request.AsSpan(8, 2)) && U16(scene, 2) == 1 && U16(scene, 4) == peer.Id &&
                    scene.AsSpan(6, 4).SequenceEqual(request.AsSpan(10, 4)),
                    "NPC470成功后23/24使用原始town/area/x/y，不夹回步行矩形或伪造额外坐标偏移");
                peer.Expect(0, 379, Effect(2, peer.Id)); peer.Expect(0, 378, Cooldown(2, 420));
                peer.Barrier();
                That(fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", peer.Id)[0]
                    .SequenceEqual(new object[] { (long)town, (long)area, (long)x, (long)y }), "实收NPC固定落点原样持久化");
                peer.Send(35, Move(x, 220)); peer.Barrier();
                That(fixture.Query("SELECT x,y FROM characters WHERE id=$p0", peer.Id) is [[long savedX, 220L]] && savedX == x,
                    "NPC传送之后可继续正常移动，不依赖重新选角或额外124解锁");
                clock.Offset += TimeSpan.FromSeconds(420);
            }
        }
        int Count() => System.Text.Json.JsonSerializer.Deserialize<ItemCore>((string)fixture.Query(
            "SELECT item_core FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=2600014")[0][0])!.Count;

        async Task SingleMemberPartyAsync(TcpPeer peer, ushort slot)
        {
            peer.Send(1426); peer.Expect(0, 23); peer.Expect(0, 24); peer.Barrier();
            CreateParty();
            var before = fixture.Query("SELECT revision,town,area,x,y FROM characters WHERE id=$p0", peer.Id)[0];
            peer.Send(434, Party(39, 3, 783, 205, 0)); peer.Expect(1, 434, [0, 19, 0, 39, 0, 0, 0, 0, 0]); peer.Barrier();
            That(before.SequenceEqual(fixture.Query("SELECT revision,town,area,x,y FROM characters WHERE id=$p0", peer.Id)[0]),
                "光环仅接入source1，不伪造公会等其他来源权限");
            using (var observer = Open(fixture, "party-observer"))
            {
                observer.Create("PartyWatch"); observer.Select(); observer.Send(1426); observer.Expect(0, 23); observer.Expect(0, 24);
                observer.Batch((0, 2), (0, 3), (0, 23), (0, 22));
                var listed = observer.Expect(0, 9).Body;
                That(listed.Length == 56 && listed[9] == 0, "同屏观察者收到统一队伍目录名册"); observer.Barrier();
                peer.Batch((0, 2), (0, 3), (0, 23), (0, 22)); peer.Barrier();
                peer.Send(434, Party(39, 3, 783, 205, 1)); peer.Expect(1, 434, [1]);
                peer.Expect(0, 379, Effect(3, peer.Id));
                var status = peer.Expect(0, 377).Body;
                That(status.Length == 30 && status[0] == 39 && status[1] == 1 && status[2] == 1 && U16(status, 3) == 0 &&
                    U16(status, 5) == peer.Id && status[7] == 1 && status[29] == 0 &&
                    Enumerable.Range(1, 7).All(index => U16(status, 5 + index * 3) == ushort.MaxValue && status[7 + index * 3] == 0),
                    "单成员按本版377的队长槽0及八行格式声明已同意，不等待不存在的队员或错用角色ID当槽");
                peer.Expect(0, 379, Effect(1, peer.Id)); peer.Expect(0, 6, Build(w => w.Write(observer.Id)));
                peer.Expect(0, 23); peer.Expect(0, 24); peer.Expect(0, 379, Effect(2, peer.Id));
                PartyInformation(); peer.Expect(0, 378, Cooldown(0, 600)); peer.Barrier();
                observer.Expect(0, 379, Effect(3, peer.Id)); observer.Expect(0, 379, Effect(1, peer.Id));
                observer.Expect(0, 6, Build(w => w.Write(peer.Id)));
                var hidden = observer.Expect(0, 9).Body;
                That(hidden.Length == 11 && hidden[9] == 3 && U16(hidden, 4) == U16(listed, 4),
                    "未查询J面板的观察者在队伍跨区后移除原同屏目录"); observer.Barrier();
            }
            That(fixture.Query("SELECT potion_until,aura_until,upgrade_aura_until,party_aura_until FROM character_teleport_cooldowns WHERE character_id=$p0", peer.Id)
                is [[0L, 0L, 0L, long until]] && until == clock.GetUtcNow().ToUnixTimeMilliseconds() + 600000 &&
                fixture.Query("SELECT revision,town,area,x,y FROM characters WHERE id=$p0", peer.Id) is [[long revision, 39L, 3L, 783L, 205L]] &&
                revision == (long)before[0] + 1,
                "组队光环只提交一次位置和PVF冷却，不增加成员、不占用个人两类计时器");
            RejectCooldown();
            peer.Send(13); peer.Expect(1, 13, [1]); peer.Expect(0, 9); peer.Barrier(); CreateParty(); RejectCooldown();
            peer.Send(7); peer.Expect(0, 9); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            var entry = peer.Select(slot, prepared: true, inventoryNotices: [(0, 378)]); peer.Barrier();
            Bytes(entry.Single(packet => packet is { Kind: 0, Opcode: 378 }).Body, Cooldown(0, 600), "重选初始化恢复组队type0冷却");
            CreateParty(); RejectCooldown();
            clock.Offset += TimeSpan.FromSeconds(600);
            peer.Send(434, Party(39, 3, 783, 205, 1)); peer.Expect(1, 434, [1]);
            peer.Expect(0, 379, Effect(3, peer.Id)); peer.Expect(0, 377); peer.Expect(0, 379, Effect(1, peer.Id));
            peer.Expect(0, 23); peer.Expect(0, 24); peer.Expect(0, 379, Effect(2, peer.Id));
            PartyInformation(); peer.Expect(0, 378, Cooldown(0, 600)); peer.Barrier();

            void CreateParty()
            {
                peer.Send(12, Convert.FromHexString("000304000000000500000000000007")); PartyInformation(); peer.Barrier();
            }
            void PartyInformation() => peer.Batch((0, 2), (0, 153), (0, 11), (0, 9), (0, 11), (0, 153), (0, 26));
            void RejectCooldown()
            {
                var saved = fixture.Query("SELECT revision,town,area,x,y FROM characters WHERE id=$p0", peer.Id)[0];
                peer.Send(434, Party(39, 3, 783, 205, 1)); peer.Expect(1, 434, [0, 22, 0, 39, 88, 2, 0, 0, 1]); peer.Barrier();
                That(saved.SequenceEqual(fixture.Query("SELECT revision,town,area,x,y FROM characters WHERE id=$p0", peer.Id)[0]),
                    "重复请求、退队重建及重选都保留真实组队冷却，434拒绝附带600秒且不再次迁移");
            }
        }
    }
    static byte[] Potion(short slot, int item, byte town, byte area, ushort x, ushort y) => Build(w =>
    { w.Write(slot); w.Write(item); w.Write((byte)0); Position(w, town, area, x, y); });
    static byte[] Solo(int character, int npc, byte town, byte area, ushort x, ushort y) => Build(w =>
    { w.Write(character); w.Write(npc); Position(w, town, area, x, y); });
    static byte[] Party(byte town, byte area, ushort x, ushort y, byte source) => Build(w =>
    { Position(w, town, area, x, y); w.Write(source); });
    static byte[] Cooldown(byte type, int seconds) => Build(w => { w.Write((byte)1); w.Write(type); w.Write(seconds); });
    static byte[] Effect(byte stage, ushort actor) => Build(w => { w.Write(stage); w.Write((byte)1); w.Write(actor); });
    static void Position(BinaryWriter w, byte town, byte area, ushort x, ushort y)
    { w.Write(town); w.Write(area); w.Write(x); w.Write(y); w.Write((byte)5); }
    sealed class Clock : TimeProvider
    {
        readonly DateTimeOffset start = DateTimeOffset.UtcNow;
        public TimeSpan Offset;
        public override DateTimeOffset GetUtcNow() => start + Offset;
    }
}
