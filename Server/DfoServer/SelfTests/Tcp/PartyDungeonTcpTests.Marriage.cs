using DfoServer.Development.Dungeons;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    public static async Task RunMarriageAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "marriage-dungeon"), resources: resources);
        await fixture.StartAsync();
        using var first = Open(fixture, "coupledungeona"); using var second = Open(fixture, "coupledungeonb");
        var peers = new[] { first, second };
        first.Create("CoupleDgnA", 0); second.Create("CoupleDgnB", 1);
        fixture.Sql("UPDATE characters SET level=30,experience=$p0 WHERE id IN ($p1,$p2)",
            SelfTestCatalogs.Growth(resources.Archive).GetLevelStartExperience(30), first.Id, second.Id);
        var repository = new CharacterRepository(fixture.Database); var owners = new AccountOwners();
        var ticket = resources.ItemResources.Items.Get(10002609);
        using (var ownerA = owners.Acquire("coupledungeona", () => { }))
        using (var ownerB = owners.Acquire("coupledungeonb", () => { }))
        {
            var a = repository.LoadCharacter(ownerA, 0); var b = repository.LoadCharacter(ownerB, 0);
            a = repository.Save(ownerA, a, new(39, 0, 2306, 186), resources.Towns.InitialReturn, new(a.Inventory, a.Inventory with
                { Items = a.Inventory.Items.Add(new(0, new(InventoryListType.Main, ItemSlotBoundService.Range(ticket.Kind, a.Inventory).Start), new(10002609, ticket.Kind, 6))) }, "marriage_fixture"));
            b = repository.Save(ownerB, b, a.Position, b.Return, new(b.Inventory, b.Inventory with
                { Items = b.Inventory.Items.Add(new(0, new(InventoryListType.Main, ItemSlotBoundService.Range(ticket.Kind, b.Inventory).Start), new(10002609, ticket.Kind, 3))) }, "marriage_fixture"));
            var record = MarriageWeddingRules.CreateEngagement(Guid.NewGuid(), 801, a.Id, b.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) with
                { State = 4, MarriedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            repository.CommitMarriageTogether(ownerA, ownerB, a, b, new(a.Inventory, a.Inventory, "marriage_fixture"),
                new(b.Inventory, b.Inventory, "marriage_fixture"), null, record);
        }
        foreach (var peer in peers) { peer.Select(resumeTown: true, room: false); Drain(peer); }
        That(peers.All(peer => peer.MarriagePartnerCard.Length == 98 && U32(peer.MarriagePartnerCard, 5) == 801),
            "夫妻首次入城已恢复伴侣卡，无需主动查询1037也具有原生NPC入场所需姓名");
        for (int i = 0; i < peers.Length; i++)
        {
            peers[i].Send(2, Build(w => { w.Write((byte)5); w.Write(0x0100007fu); w.Write(0x0100007fu); w.Write((ushort)(30100 + i)); w.Write(512u); }));
            Drain(peers[i]);
        }
        first.Send(10, Build(w => { w.Write(second.Id); w.Write((byte)0); w.Write(0u); w.Write(new byte[6]); }));
        Drain(first); Drain(second);
        second.Send(11, Build(w => { w.Write(first.Id); w.Write((byte)0); w.Write(0u); }));
        That(All(second).All(rows => rows.Any(p => p.Opcode == 9)), "夫妻通过正式邀请组成双人队伍");
        first.Send(15, BitConverter.GetBytes(3541u));
        var selectionOpened = All();
        That(selectionOpened.All(rows => rows.Any(p => p.Kind == 1 && p.Opcode == 15)), "婚礼NPC非零15入口为双方建立正式选图");
        That(selectionOpened.All(rows => rows.Count(p => p is { Kind: 0, Opcode: 27, Body.Length: 42 }) == 1 &&
            !rows.Any(p => p.Opcode is 28 or 29)),
            "指定NPC副本也先以一次42B空27为每位成员建立原生scene2，不靠伪造加载完成越过屏障");
        byte[] selection = Coop.Selection(3541, 0, 2);
        first.Send(16, selection);
        var entry = All(); var map = entry[0].Single(p => p.Opcode == 29).Body;
        That(entry.All(rows => rows.Any(p => p.Opcode == 28) && rows.Any(p => p.Opcode == 29)) && TicketCount(first) == 3 && TicketCount(second) == 0,
            "真实3541选图成功，全员地图发布前各扣三张票");
        var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(3541).Mazes[entry[0].Single(p => p.Opcode == 28).Body[7]];
        var visited = new HashSet<DungeonPoint>(); bool cleared = false; int pickups = 0;
        for (int room = 0; room < 40 && !cleared; room++)
        {
            first.Send(37, new byte[8]); All(); second.Send(37, new byte[8]);
            That(All(second).All(rows => rows.Any(p => p.Opcode == 30)), "夫妻各自完成正式加载后释放战斗屏障");
            var point = new DungeonPoint(map[0], map[1]); visited.Add(point);
            if (map.Length > 18)
                foreach (int offset in Enumerable.Range(0, map[18]).Select(i => 19 + i * 22).Where(i => map[i + 13] != 9 && map[i + 14] == 0).OrderBy(i => map[i + 13] == 3))
                {
                    ushort key = U16(map, offset + 6);
                    first.Send(39, FormalDungeonTcpTests.Death(key, first.Id, map[offset + 12]));
                    var death = All();
                    That(death.All(rows => rows.Any(p => p.Opcode == 38 && U16(p.Body) == key)), "正式夫妻副本怪物死亡同步双方");
                    // 第二人不拾取本图掉落的门票，保留零票状态验证下一次共同入场拒绝。
                    var loot = death[0].Single(p => p.Opcode == 38 && U16(p.Body) == key).Body;
                    for (int row = 0; row < U16(loot, 2); row++)
                    {
                        uint ground = U32(loot, 4 + row * 131);
                        first.Send(43, Build(w => { w.Write(ground); w.Write(new byte[16]); }));
                        That(All()[0].Any(p => p is { Kind: 1, Opcode: 43, Body: [1] }), "夫妻副本本图专属地面掉落可真实领取"); pickups++;
                    }
                    if (map[offset + 13] == 3) { cleared = true; break; }
                }
            if (cleared) break;
            var edges = maze.StaticTopology.Links!.SelectMany(link => new[] { (link.First, link.Second), (link.Second, link.First) }).ToArray();
            var next = edges.Where(edge => edge.Item1 == point).Select(edge => edge.Item2).OrderBy(p => visited.Contains(p)).First();
            byte[] move = new byte[100]; move[0] = checked((byte)next.X); move[1] = checked((byte)next.Y);
            first.Send(45, move); map = All()[0].Single(p => p.Opcode == 29).Body;
        }
        That(cleared && pickups > 0 && fixture.Query("SELECT count(*) FROM character_dungeon_fatigue_receipts") is [[0L]] &&
            fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE amount<>0") is [[0L]],
            "夫妻副本真实击杀到BOSS，无疲劳和普通怪物经验");
        foreach (var peer in peers) { peer.Send(42, []); That(Drain(peer).Any(p => p is { Kind: 1, Opcode: 42, Body: [1] }), "夫妻副本正常退场"); }
        All();
        foreach (var peer in peers) { peer.Send(13, []); Drain(peer); }
        All();
        first.Send(10, Build(w => { w.Write(second.Id); w.Write((byte)0); w.Write(0u); w.Write(new byte[6]); })); All();
        second.Send(11, Build(w => { w.Write(first.Id); w.Write((byte)0); w.Write(0u); }));
        That(All(second).All(rows => rows.Any(p => p.Opcode == 9)) && TicketCount(second) == 0,
            "正式退场后重建有效双人队伍，第二人保持真实零门票");
        first.Send(15, BitConverter.GetBytes(3541u));
        That(All().All(rows => rows.Any(p => p is { Kind: 1, Opcode: 15 } && p.Body[0] == 1)),
            "有效双人队伍通过NPC重新打开夫妻副本入口，再验证资产不足");
        long beforeTickets = TicketCount(first);
        first.Send(16, selection); var denied = All();
        That(denied[0].Any(p => p is { Kind: 1, Opcode: 16 } && p.Body[0] == 0) &&
            !denied.Any(rows => rows.Any(p => p.Opcode is 28 or 29)) && TicketCount(first) == beforeTickets && TicketCount(second) == 0,
            "一方缺三张门票拒绝再次进入，不单边扣另一方票");
        That(fixture.Query("PRAGMA foreign_key_check").Length == 0, "夫妻副本隔离库外键完整");
        Console.WriteLine($"INFO marriage dungeon pickups: {pickups}");
        Console.WriteLine($"PASS {Count - checks} marriage dungeon TCP assertions");

        WirePacket[][] All(TcpPeer? requester = null)
        {
            requester ??= first;
            var result = new Dictionary<TcpPeer, WirePacket[]> { [requester] = Drain(requester) };
            foreach (var peer in peers.Where(peer => !ReferenceEquals(peer, requester))) result.Add(peer, Drain(peer));
            return peers.Select(peer => result[peer]).ToArray();
        }
        long TicketCount(TcpPeer peer) => Convert.ToInt64(fixture.Query("SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=10002609", peer.Id).Single()[0]);
    }
}
