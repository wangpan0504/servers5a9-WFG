using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    internal static void VerifyTrainingPartyEntry(TcpTestFixture fixture, TcpTestResources resources)
    {
        // 使用真实39/0→102→5000入口，不能注入选图列表绕过原来可达的组队15路径。
        var area = resources.Towns.Areas[(39, 0)];
        That(area.DungeonWorldMap == 102 && resources.Towns.DungeonMaps[102] is { Error: null } map &&
            map.Options.Any(option => option.DungeonId == 5000 && option.QuestId == -1),
            "真实赫顿玛尔区域保留修炼场5000的指定副本入口");
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Append(3156).ToHashSet();
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(39, 0, checked((ushort)(bounds.X + bounds.Width / 2)),
            checked((ushort)(bounds.Y + bounds.Height / 2)));
        That(resources.Towns.SelectionReturn(position, done) is not null,
            "真实39/0位置具有合法选图返回点，拒绝不能由缺少入口资格造成");
        byte[] request = Convert.FromHexString("881300000000000001FFFF0000000000000000000000000000");

        foreach (int requester in new[] { 0, 1 })
        {
            using var group = new Coop(fixture, resources, "train" + requester, position, done.ToArray(), 95, 2);
            group.Join();
            string before = State();
            var peer = group.Peers[requester];
            // 匹配原生sender：16可能在收到15应答前已排队，不在两请求间插入屏障。
            peer.Send(15, Convert.FromHexString("88130000"));
            peer.Send(16, request);
            var rejected = group.All(peer);
            Console.WriteLine("training_party_entry " + System.Text.Json.JsonSerializer.Serialize(new
            {
                Requester = requester, Town = 39, Area = 0, WorldMap = 102, Dungeon = 5000,
                Packets = rejected.Select(rows => rows.Select(packet => new
                    { packet.Kind, packet.Opcode, Body = Convert.ToHexString(packet.Body) }).ToArray()).ToArray(),
                PersistentStateUnchanged = State() == before
            }));
            That(rejected[requester].Count(packet => packet is { Kind: 1, Opcode: 15, Body: [0, 1, 0] }) == 1 &&
                rejected[requester].Count(packet => packet is { Kind: 1, Opcode: 16, Body: [0, 9, 0, 0] }) == 1 &&
                rejected.All(rows => rows.All(packet => packet is not { Kind: 0, Opcode: 27 or 28 or 29 } &&
                    packet is not { Kind: 0, Opcode: 3, Body: [1, _, _, 1] })) && State() == before,
                "城镇多人修炼场在15前置拒绝，排队16不创建选图/Run、不扣费、不改变任一成员状态和返回点");

            peer.Send(132);
            That(group.All(peer).All(rows => rows.All(packet => packet.Opcode is not (132 or 24 or 27))),
                "拒绝后没有遗留MapSelect上下文，132不能凭空退城或重开选图");
            group.Open();
            group.Peers[0].Send(15, Convert.FromHexString("88130000"));
            var confirmed = group.All();
            That(confirmed[0].Count(packet => packet is { Kind: 1, Opcode: 15, Body: [1] }) == 1 &&
                confirmed.All(rows => rows.All(packet => packet is not { Kind: 0, Opcode: 27 or 28 or 29 })),
                "已有合法组队MapSelect仍可确认，不能由新城镇入口规则清空或强退");
            group.Peers[0].Send(16, request);
            var unavailable = group.All();
            That(unavailable[0].Any(packet => packet is { Kind: 1, Opcode: 16, Body: [0, 9, 0, 0] }) && State() == before &&
                unavailable.All(rows => rows.All(packet => packet is not { Kind: 0, Opcode: 27 or 28 or 29 })),
                "既有选择中的多人修炼场仍按原16拒绝，原选择上下文及持久状态保留");
            group.Peers[0].Send(132);
            That(group.All().All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 24 })),
                "拒绝后仍可打开普通选图并通过队长132正常返回城镇");
            foreach (var member in group.Peers)
                That(fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", member.Id) is
                    [[39L, 0L, long x, long y]] && x == position.X && y == position.Y,
                    "合法选图取消恢复各成员原入口，不能保存修炼场或虚假返回位置");
        }
        using (var group = new Coop(fixture, resources, "trainone", position, done.ToArray(), 95, 1))
        {
            group.Join();
            var peer = group.Peers[0];
            peer.Send(12, Convert.FromHexString("000304000000000500000000000007"));
            var created = group.All()[0].Single(packet => packet is { Kind: 0, Opcode: 9 }).Body;
            That(created.Length == 56 && created[37] == 1 && U16(created, 39) == peer.Id,
                "单成员边界通过真实12建立Party，不能拿未组队独行替代");
            string fatigue = fixture.Snapshot("SELECT * FROM character_fatigue WHERE character_id=$p0", peer.Id);
            string assets = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id);
            peer.Send(15, Convert.FromHexString("88130000"));
            peer.Send(16, request);
            var entered = group.All()[0];
            That(entered.Where(packet => packet.Opcode is 15 or 27 or 28 or 29)
                    .Select(packet => (packet.Kind, packet.Opcode))
                    .SequenceEqual(new (byte, ushort)[] { (1, 15), (0, 27), (0, 28), (0, 29) }) &&
                entered.Single(packet => packet is { Kind: 1, Opcode: 15 }).Body.SequenceEqual(new byte[] { 1 }) &&
                entered.Single(packet => packet is { Kind: 0, Opcode: 27 }).Body.SequenceEqual(new byte[42]) &&
                U32(entered.Single(packet => packet is { Kind: 0, Opcode: 28 }).Body) == 5000 &&
                U32(entered.Single(packet => packet is { Kind: 0, Opcode: 29 }).Body, 14) == 36250,
                "真单成员Party保留15成功及27→28→29实际修炼场，不被多人拒绝规则误伤");
            group.Load();
            peer.Send(37, new byte[8]);
            group.All();
            That(fixture.Query("SELECT last_attempt,next_object_key FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[1L, 256L]] &&
                fixture.Query("SELECT count(*) FROM character_dungeon_fatigue_receipts WHERE character_id=$p0", peer.Id) is [[0L]],
                "单成员Party加载和重报只安装一份尝试，不扣训练疲劳");
            peer.Send(42);
            var returned = group.All()[0];
            That(returned.Any(packet => packet is { Kind: 1, Opcode: 42, Body: [1] }) &&
                returned.Any(packet => packet is { Kind: 0, Opcode: 24 }) &&
                fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", peer.Id) is
                    [[39L, 0L, long x, long y]] && x == position.X && y == position.Y &&
                fixture.Snapshot("SELECT * FROM character_fatigue WHERE character_id=$p0", peer.Id) == fatigue &&
                fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id) == assets,
                "单成员Party修炼返回真实入口，保留疲劳与原UID资产");
        }
        Console.WriteLine("PASS 多人修炼场真实入口拒绝、排队16、队员边界、合法选图恢复及单成员Party成功TCP");

        string State() => fixture.Snapshot() +
            fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") +
            fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid");
    }
}
