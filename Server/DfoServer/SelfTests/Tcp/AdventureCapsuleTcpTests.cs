using Microsoft.Data.Sqlite;
using System.Buffers.Binary;
using DfoServer.Game.Adventure;
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
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "adventure-capsules"), resources: resources);
        await fixture.StartAsync();
        byte level = checked((byte)catalog.Capsule.MinimumLevel);
        ulong initial = growth.GetLevelStartExperience(level);
        using (var peer = Open(fixture, "capstcp"))
        {
            peer.Send(1499, []); peer.Expect(1, 1499, [0, 19, 0]);
            peer.Create("CapLow"); peer.Create("CapTcp");
            Execute("UPDATE characters SET level=" + level + ",experience=" + initial + " WHERE name='CapTcp'");
            // 同步写入当前周四边界，避免载入时按公告把种下的胶囊进度清零。
            Execute("UPDATE account_adventures SET capsule_experience=" + catalog.CapsuleCapacity +
                ",capsule_week=" + AdventurePeriod.CapsuleWeek(catalog, DateTimeOffset.UtcNow));
            peer.Select(0);
            peer.Send(1499, []); peer.Expect(1, 1499, [0, 14, 0]); peer.Barrier();
        }
        using (var peer = Open(fixture, "capstcp"))
        {
            peer.Select(1);
            That(U64(peer.AdventureInfo, 18 + 7424) == catalog.CapsuleCapacity &&
                U64(peer.AdventureInfo, 18 + 7424) / catalog.Capsule.GaugeExperience == catalog.Capsule.MaximumGauges &&
                U32(peer.AdventureInfo, 18 + 7432) == catalog.Capsule.GrantedExperience &&
                U32(peer.AdventureRefresh, 6 + 32) == catalog.Capsule.GrantedExperience,
                "同账号换角色读取全部十颗共享胶囊，两包确认窗预览均为单颗真实经验");
            peer.Send(1499, []); peer.Expect(1, 1499, [1]);
            var experience = peer.Expect(0, 37).Body;
            That(U64(experience, 1) == initial + catalog.Capsule.GrantedExperience, "1499成功后37回放实际绝对XP");
            bool finished = false;
            for (int count = 0; count < 10; count++)
            {
                var packet = peer.Read();
                if (packet is { Kind: 0, Opcode: 1343 })
                {
                    var refresh = peer.Expect(0, 1349); finished = true;
                    That(U64(packet.Body, 18 + 7424) == catalog.CapsuleCapacity - catalog.Capsule.GaugeExperience &&
                        U64(packet.Body, 18 + 7424) / catalog.Capsule.GaugeExperience == catalog.Capsule.MaximumGauges - 1 &&
                        U32(packet.Body, 18 + 7432) == catalog.Capsule.GrantedExperience &&
                        U32(refresh.Body, 6 + 32) == catalog.Capsule.GrantedExperience,
                        "首次领取后账号仍保留九颗胶囊，两包仍预览下一颗真实经验");
                    // 客户端0102F910把1349 RAW+16原样写管理器+0x190，01034220在1343里用经验表按level+1算同一字段，
                    // 提取后紧跟的1349必须回放同一个分母，否则页面的「下一级所需经验」被覆盖成0。
                    That(U64(refresh.Body, 6 + 16) == catalog.Experience[checked((int)U32(packet.Body, 18)) + 1],
                        "1349 RAW+16回放下一级所需经验，与1343按经验表算出的一致"); break;
                }
                That(packet.Kind == 0 && packet.Opcode is 2 or 19 or 21 or 356, "胶囊只追加现有技能、等级与任务投影，不切场景");
            }
            That(finished, "最终账号信息必须收到，不能无限等待");
            for (uint claimed = 2; claimed <= catalog.Capsule.MaximumGauges; claimed++)
            {
                peer.Send(1499, []); peer.Expect(1, 1499, [1]);
                That(U64(peer.Expect(0, 37).Body, 1) == initial + claimed * (ulong)catalog.Capsule.GrantedExperience,
                    "连续提取每次只发一颗真实经验");
                bool refreshed = false;
                for (int count = 0; count < 10; count++)
                {
                    var packet = peer.Read();
                    if (packet is { Kind: 0, Opcode: 1343 })
                    {
                        var refresh = peer.Expect(0, 1349);
                        ulong remaining = catalog.CapsuleCapacity - claimed * catalog.Capsule.GaugeExperience;
                        uint preview = remaining == 0 ? 0 : catalog.Capsule.GrantedExperience;
                        That(U64(packet.Body, 18 + 7424) == remaining && U64(refresh.Body, 6 + 24) == remaining &&
                            remaining / catalog.Capsule.GaugeExperience == catalog.Capsule.MaximumGauges - claimed &&
                            U32(packet.Body, 18 + 7432) == preview && U32(refresh.Body, 6 + 32) == preview,
                            "1343与1349计量逐颗递减，单次预览与真实奖励一致，最后一颗领完归零");
                        refreshed = true; break;
                    }
                    That(packet.Kind == 0 && packet.Opcode is 2 or 19 or 21 or 356, "连续胶囊保持原有投影顺序");
                }
                That(refreshed, "连续胶囊必须收到最终账号同步");
            }
            peer.Send(1499, []); peer.Expect(1, 1499, [0, 3, 0]); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM account_adventure_receipts WHERE action='capsule_claim'") is [[long receipts]] &&
                receipts == catalog.Capsule.MaximumGauges, "十颗领完后重复请求不再次发经验");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("capsules-restarted", isolatedProcess: true);
        using (var peer = Open(fixture, "capstcp"))
        {
            peer.Select(1);
            That(fixture.Query("SELECT experience FROM characters WHERE name='CapTcp'") is [[long xp]] &&
                xp == (long)(initial + catalog.Capsule.MaximumGauges * (ulong)catalog.Capsule.GrantedExperience) &&
                U64(peer.AdventureInfo, 18 + 7424) == 0 && U32(peer.AdventureInfo, 18 + 7432) == 0 &&
                U32(peer.AdventureRefresh, 6 + 32) == 0, "跨进程恢复实际角色经验和扣除后的账号胶囊，空囊预览为零");
        }
        Console.WriteLine($"PASS {Count - checks} 账号胶囊领取、资格拒绝、XP包序和冷启动TCP断言");
        void Execute(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False;Foreign Keys=True"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }
}
