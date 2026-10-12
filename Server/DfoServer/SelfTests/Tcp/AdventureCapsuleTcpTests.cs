using Microsoft.Data.Sqlite;
using System.Buffers.Binary;
using DfoServer.GameWorld.Characters;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class AdventureCapsuleTcpTests
{
    static ulong U64(byte[] body, int at) => BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(at));
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        AdventureCapsuleTests.RunResources(resources.Archive, resources.Learning, resources.Adventure);
        int checks = Count;
        var catalog = resources.Adventure;
        var growth = CharacterGrowthCatalog.Load(resources.Archive, resources.Learning);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "adventure-capsules"), resources: resources);
        await fixture.StartAsync();
        byte level = checked((byte)catalog.Capsule.MinimumLevel);
        ulong initial = growth.GetLevelStartExperience(level);
        using (var peer = Open(fixture, "capstcp"))
        {
            peer.Send(1499, []); peer.Expect(1, 1499, [0, 19, 0]);
            peer.Create("CapLow"); peer.Create("CapTcp");
            Execute("UPDATE characters SET level=" + level + ",experience=" + initial + " WHERE name='CapTcp'");
            Execute("UPDATE account_adventures SET capsule_experience=" + catalog.Capsule.MinimumExperience);
            peer.Select(0);
            peer.Send(1499, []); peer.Expect(1, 1499, [0, 14, 0]); peer.Barrier();
        }
        using (var peer = Open(fixture, "capstcp"))
        {
            peer.Select(1);
            That(U64(peer.AdventureInfo, 18 + 7424) == catalog.Capsule.MinimumExperience, "同账号换角色读取共享胶囊，不从角色旧胶囊字段读取");
            peer.Send(1499, []); peer.Expect(1, 1499, [1]);
            var experience = peer.Expect(0, 37).Body;
            That(U64(experience, 1) == initial + catalog.Capsule.GrantedExperience, "1499成功后37回放实际绝对XP");
            bool finished = false;
            for (int count = 0; count < 10; count++)
            {
                var packet = peer.Read();
                if (packet is { Kind: 0, Opcode: 1343 })
                {
                    peer.Expect(0, 1349); finished = true;
                    That(U64(packet.Body, 18 + 7424) == 0, "账号与Actor回放扣除后的胶囊进度"); break;
                }
                That(packet.Kind == 0 && packet.Opcode is 2 or 19 or 21 or 356, "胶囊只追加现有技能、等级与任务投影，不切场景");
            }
            That(finished, "最终账号信息必须收到，不能无限等待");
            peer.Send(1499, []); peer.Expect(1, 1499, [0, 3, 0]); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM account_adventure_receipts WHERE action='capsule_claim'") is [[1L]], "进度不足后重复请求不再次发经验");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("capsules-restarted", isolatedProcess: true);
        using (var peer = Open(fixture, "capstcp"))
        {
            peer.Select(1);
            That(fixture.Query("SELECT experience FROM characters WHERE name='CapTcp'") is [[long xp]] && xp == (long)(initial + catalog.Capsule.GrantedExperience) &&
                U64(peer.AdventureInfo, 18 + 7424) == 0, "跨进程恢复实际角色经验和扣除后的账号胶囊");
        }
        Console.WriteLine($"PASS {Count - checks} 账号胶囊领取、资格拒绝、XP包序和冷启动TCP断言");
        void Execute(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False;Foreign Keys=True"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }
}
