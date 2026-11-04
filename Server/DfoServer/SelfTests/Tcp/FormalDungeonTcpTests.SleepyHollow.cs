using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    // 真实socket只走本次实际随机路线；全部五候选及SQL故障由独立Session资源回归覆盖。
    public static async Task RunSleepyHollowAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "sleepy-hollow-socket"), resources: resources);
        await fixture.StartAsync();
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 7322));
        var done = new HashSet<int>(area.Quests) { 6030, 6082 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
            checked((ushort)(bounds.Y + bounds.Height / 2)));
        Character character;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("SleepyTcp", resources.Characters.Get(0), position, position, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
            character = new CharacterRepository(fixture.Database).Create(owner, candidate with
                { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = done.ToArray(), InitialLearning = learning });
        }
        using var peer = Open(fixture);
        peer.Select(character.Slot, prepared: true, resumeTown: true, room: false);
        SelectMap(peer, incompleteHellQuest: HasIncompleteHellQuest(resources, character), daybreakRemaining: 2);
        var request = new byte[25];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 7322);
        request[9] = request[10] = byte.MaxValue;
        peer.Send(16, request);
        var information = peer.Expect(0, 28).Body;
        var map = peer.Expect(0, 29).Body;
        That(U32(information, 0) == 7322 && information[4] == 0, "断头谷真实16入场保留固定难度0");
        var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(7322).Mazes.Single(row => row.SourceOrdinal == information[7]);
        var route = QuestStoryRouteTests.Path(maze.StaticTopology, new(map[0], map[1]), new(information[8], information[9]));
        int junk = 0, seeds = 0, bosses = 0;
        for (int room = 0; room < route.Length; room++)
        {
            if (room > 0)
            {
                var move = new byte[100]; move[0] = (byte)route[room].X; move[1] = (byte)route[room].Y;
                peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
            }
            Load(peer, fatigue: false); peer.Expect(0, 36);
            foreach (int offset in Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
                .Where(offset => map[offset + 13] != 9 && map[offset + 14] == 0).OrderBy(offset => map[offset + 13] == 3))
            {
                ushort key = U16(map, offset + 6);
                var report = Death(key, peer.Id, map[offset + 12]);
                peer.Send(39, report);
                var death = peer.Expect(0, 38).Body;
                That(U16(death, 0) == key, "真实39报告后的38掉落通知绑定实际怪物key");
                bool boss = map[offset + 13] == 3;
                int dropJunk = 0, dropSeeds = 0;
                for (int index = 0; index < U16(death, 2); index++)
                {
                    int at = 4 + index * 131;
                    if (U32(death, at + 6) == SleepyHollowMaterialDropPolicy.UsefulJunkId) dropJunk += checked((int)U32(death, at + 10));
                    if (U32(death, at + 6) == SleepyHollowMaterialDropPolicy.BirthSeedId) dropSeeds += checked((int)U32(death, at + 10));
                }
                That(boss ? dropJunk == 9 && dropSeeds is >= 10 and <= 14 : dropJunk == 0 && dropSeeds == 0,
                    "真实socket仅Boss掉9杂物和10至14芽，普通怪不补材料");
                if (boss)
                {
                    bosses++;
                    That(U32(map, offset + 8) is >= 107010048 and <= 107010052, "socket实收断头谷真实Boss模板");
                    peer.Expect(0, 115, [1, 1, (byte)key, (byte)(key >> 8)]);
                    peer.Expect(0, 31, []);
                }
                junk += dropJunk; seeds += dropSeeds;
                PickupResourceDrops(fixture, peer, death, 0);
                string saved = fixture.Snapshot();
                peer.Send(39, report); peer.Barrier();
                That(fixture.Snapshot() == saved, "真实socket重复39不能重发材料，重复43由共用拾取断言覆盖");
            }
        }
        long Amount(int id) => Convert.ToInt64(fixture.Query("SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1", peer.Id, id)[0][0]);
        That(bosses == 1 && junk == 9 && seeds is >= 10 and <= 14 &&
            Amount(SleepyHollowMaterialDropPolicy.UsefulJunkId) == junk && Amount(SleepyHollowMaterialDropPolicy.BirthSeedId) == seeds,
            "真实16/39/38/43链路完成一次Boss奖励并精确入库");
        Console.WriteLine($"PASS 断头谷socket实际路线{maze.SourceOrdinal}：杂物{junk}，芽{seeds}，死亡/拾取重放不增资产");
    }
}
