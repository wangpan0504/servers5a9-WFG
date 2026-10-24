using System.Buffers.Binary;
using DfoServer.Development.Dungeons;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunPassiveAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        DungeonPassiveDropTests.RunResources(resources);
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var maze = dungeons.Get(3).Mazes.Single(row => row.ConnectsQuest(3145));
        var start = maze.MapSpecification!.Single(map => map.Position == maze.Starts.Single());
        if (!SelfTestCatalogs.Rooms(resources.Archive).Get(start).SpecialObjects.Any(parent => parent.Children.Any(child => child.Kind == "[item]")))
        {
            Console.WriteLine("SKIP 特殊树TCP：当前PVF的3145入口未声明item测试树；规则及SQLite覆盖已执行");
            return;
        }
        int before = Count;
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "dungeon-passive-items"), resources: resources);
        await fixture.StartAsync();
        using var peer = Open(fixture);
        peer.Create("PassiveTcp");
        peer.Select();
        EntryTown(peer);
        byte[] map = Enter(peer);
        int at = 19 + map[18] * 22, count = map[at];
        That(map.Length == at + 4 + 21 * count && map[^1] == 255,
            "生产Runtime入房按21B物品列表发布，演员和三字节尾部完整保留");
        That(Convert.ToInt32(fixture.Query("SELECT count(*) FROM character_dungeon_passive_pickups WHERE character_id=$p0", peer.Id)[0][0]) == 0,
            "真实入房不保存特殊物品生成记录，只有成功拾取才写回执");
        var keys = new HashSet<uint>();
        for (int index = 0; index < count; index++)
        {
            int offset = at + 1 + index * 21;
            uint key = U32(map, offset + 1);
            That(keys.Add(key) && U16(map, offset + 18) == 0 && map[offset + 20] == 0,
                "特殊物品地面key、可见性和尾部绑定实际入房数据");
            byte[] pickup = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(pickup, key);
            peer.Send(43, pickup);
            peer.Expect(1, 43, [0, 19, 0]);
        }
        Load(peer);
        int picked = 0;
        for (int index = 0; index < count; index++)
        {
            int offset = at + 1 + index * 21;
            uint key = U32(map, offset + 1), item = U32(map, offset + 5);
            byte[] pickup = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(pickup, key);
            peer.Send(43, pickup);
            var notice = peer.Expect(0, 39).Body;
            That(U32(notice, 0) == key && notice.Length == (item == 0 ? 55 : 19), "无需伪造树死亡报告即可拾取原入房物品");
            peer.Expect(0, 14);
            peer.Expect(1, 43, [1]);
            peer.Send(43, pickup);
            peer.Expect(1, 43, [0, 19, 0]);
            peer.Barrier();
            That(Convert.ToInt32(fixture.Query("SELECT count(*) FROM character_dungeon_passive_pickups WHERE character_id=$p0", peer.Id)[0][0]) == 0,
                "真实TCP同key重复拾取由当前实例缓存拒绝，不新增SQL临时回执");
            picked++;
        }
        That(Convert.ToInt32(fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0", peer.Id)[0][0]) == 0,
            "特殊树入房与拾取不制造战斗死亡或经验来源");
        Console.WriteLine($"PASS {Count - before} 特殊物品真实TCP断言；入房{count}件，拾取{picked}件（未操作客户端）");
    }
}
