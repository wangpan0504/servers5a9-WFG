using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class RevisedBeastTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var f = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DfoServer.Development.Dungeons.DungeonEntryProbeMode.Disabled }, Path.Combine(root, "revised-beast"), resources: resources);
        const string account = "beast-tcp";
        Character[] roles;
        using (var owner = new AccountOwners().Acquire(account, () => { }))
        {
            var repository = new CharacterRepository(f.Database, resources.Quests);
            roles = Enumerable.Range(0, 2).Select(index =>
            {
                var seed = CharacterCreation.Prepare("BeastTcp" + (char)('A' + index), resources.Characters.Get(0),
                    resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get);
                var role = repository.Create(owner, seed with
                {
                    Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [5927],
                    ActiveMainQuests = index == 0 ? [] : [new(0, 5970, 0) { AcceptanceId = Guid.NewGuid() }],
                    InitialLearning = SkillPointLedger.Synchronize(0, 0, 95, seed.InitialLearning with
                        { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning)
                });
                var inventory = new InventoryService(resources.ItemResources.Items, resources.ItemResources.GoldLimits,
                    resources.ItemResources.EpicPieces);
                return InventoryService.Commit(repository, owner, role, inventory.Grant(role, [new(10303752, 20)]));
            }).ToArray();
        }
        await f.StartAsync();
        foreach (var role in roles)
        {
            string ReturnSnapshot() => f.Snapshot("SELECT return_town,return_area,return_x,return_y,return_direction FROM characters WHERE id=$p0", role.Id);
            string ordinaryReturn = ReturnSnapshot();
            using (var peer = Open(f, account, gamePort: ChannelTcpTests.Endpoints(f)[49], channelNumber: 49))
            {
                That(peer.LoginEndpoint[3] == 40, "魔兽49号入口登录投影真实type40");
                peer.Select(role.Slot, prepared: true, nativeSwitch: true);
                peer.Barrier();
                That(f.Query("SELECT town,area FROM characters WHERE id=$p0", role.Id) is [[38L, 1L]] && ReturnSnapshot() == ordinaryReturn,
                    "魔兽选角进入共享赛丽亚房间38/1，完整普通返回地区保持不变");
                var done = new HashSet<int> { 5927 };
                var street = ChannelAreaLanding.DecideArea(resources.Towns, 51, 1, 95, done).Spot!;
                // bottom发送空body1426，沿真实频道出房规则解析落点，不用普通36模拟出房。
                peer.Travel(51, 1, street.X, street.Y, 38, 1, bottom: true);
                That(f.Query("SELECT town,area FROM characters WHERE id=$p0", role.Id) is [[51L, 1L]] && ReturnSnapshot() == ordinaryReturn,
                    "1426以23/24直达魔兽街道51/1，不覆盖普通返回地区");
                var waiting = resources.Towns.Entrance(street, new Location(51, 2, 0, 0), done)!;
                peer.Travel(51, 2, waiting.X, waiting.Y, 51, 1);
                That(f.Query("SELECT town,area FROM characters WHERE id=$p0", role.Id) is [[51L, 2L]] && ReturnSnapshot() == ordinaryReturn,
                    "进入魔兽候战区51/2仍保留完整普通返回地区");
                string active = f.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0", role.Id);
                peer.Send(15, new byte[4]);
                peer.Expect(1, 15, [1]);
                peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
                peer.Expect(0, 1444, [0,0,0,0,0,0,0,0,2,4,2,4]);
                byte[] selection = peer.Expect(0, 27).Body;
                int dungeon = RevisedBeastSelection.DailyDungeon(f.UtcNow);
                That(selection.Length == 46 && selection[41] == 1 && U32(selection, 42) == dungeon,
                    "真实选图只发布业务日唯一魔兽图");
                byte[] entry = new byte[25];
                BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)dungeon);
                entry[4] = checked((byte)role.Slot); entry[9] = entry[10] = 255;
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(16), role.Slot == 0 ? 0u : 5970u);
                peer.Send(16, entry);
                peer.Expect(0, 1444, [1,0,0,0,1,0,0,0,2,4,2,4]);
                var info = peer.Expect(0, 28).Body;
                That(U32(info) == dungeon && info[4] == role.Slot, "0和5970上下文均进入真实对应难度实例");
                peer.Expect(0, 29);
                // 正式装配在入场后发布实际记次，任务记录不得因兼容入口被改写。
                peer.Barrier(packet => That(packet.Opcode is 1444 or 14 or 36 or 550,
                    $"入场仅接收费用和次数投影，实际{packet.Opcode}: {Convert.ToHexString(packet.Body)}"));
                That(f.Query("SELECT daily_used,weekly_used FROM character_dungeon_incount WHERE character_id=$p0 AND dungeon=5335", role.Id)
                    is [[1L, 1L]], "五图入场提交共享5335账本");
                That(f.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0", role.Id) == active,
                    "5970接取身份与材料进度不因入图被清除或自动完成");
                peer.Send(42);
                peer.Expect(1, 42, [1]);
                peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
                peer.Expect(0, 24);
                peer.Expect(0, 21);
                peer.Expect(0, 356);
                peer.Barrier();
                That(f.Query("SELECT town,area FROM characters WHERE id=$p0", role.Id) is [[51L, 2L]] && ReturnSnapshot() == ordinaryReturn,
                    "副本退场恢复魔兽候场区并保留普通返回地区，仍可继续使用频道");
            }
            using var ordinary = Open(f, account);
            ordinary.Select(role.Slot, prepared: true, nativeSwitch: true);
            ordinary.Barrier();
            That(f.Query("SELECT town,area FROM characters WHERE id=$p0", role.Id) is [[38L, 1L]] && ReturnSnapshot() == ordinaryReturn,
                "离开魔兽频道回普通共享房间及原返回地区，不复用51号快照");
        }
    }
}
