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
    public static async Task RunRegionalDropsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        foreach (int dungeonId in new[] { 310, 291100268 })
        {
            await using var fixture = new TcpTestFixture(source, Path.Combine(root, "regional-" + dungeonId), resources: resources);
            Directory.CreateDirectory(fixture.Options.DataDirectory);
            // 隔离夹具把材料倍率设为10，以确定性验证1000/10000的地区触发，不修改线上概率。
            File.WriteAllText(Path.Combine(fixture.Options.DataDirectory, "drop-control.json"),
                "{\"Enabled\":true,\"Rules\":[{\"Scope\":\"All\",\"DungeonIds\":[],\"Multipliers\":{\"Gold\":0,\"Materials\":10,\"Equipment\":0,\"MonsterItems\":0,\"Special\":0}}]}");
            await fixture.StartAsync();
            var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeonId));
            var done = resources.Quests.All.Select(quest => quest.Id).ToHashSet();
            // 普通掉落夹具不完成星空前置，避免额外随机星空选图字段干扰本组普通27合同。
            done.ExceptWith(resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].HellLegendQuests);
            var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
                checked((ushort)(bounds.Y + bounds.Height / 2)));
            Character character;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var candidate = CharacterCreation.Prepare(dungeonId == 310 ? "MetroDrop" : "HarlemDrop",
                    resources.Characters.Get(0), position, position, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
                character = new CharacterRepository(fixture.Database).Create(owner, candidate with
                    { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = done.ToArray(), InitialLearning = learning });
            }
            int picked = 0;
            using (var peer = Open(fixture))
            {
                peer.Select(character.Slot, prepared: true, resumeTown: true, room: false);
                SelectMap(peer, incompleteHellQuest: HasIncompleteHellQuest(resources, character),
                    daybreakRemaining: resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.Any(option => option.DungeonId == 530)
                        ? (byte)2 : null);
                var request = new byte[25];
                BinaryPrimitives.WriteUInt32LittleEndian(request, checked((uint)dungeonId));
                request[9] = request[10] = byte.MaxValue;
                peer.Send(16, request);
                var information = peer.Expect(0, 28).Body;
                var map = peer.Expect(0, 29).Body;
                That(U32(information, 0) == dungeonId, "地区材料走真实普通16入场及Runtime装配");
                Load(peer, fatigue: false); peer.Expect(0, 36);
                foreach (int offset in Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
                    .Where(offset => map[offset + 13] < 3 && map[offset + 14] == 0).Take(2))
                {
                    ushort key = U16(map, offset + 6);
                    var report = Death(key, peer.Id, map[offset + 12]);
                    peer.Send(39, report);
                    var death = peer.Expect(0, 38).Body;
                    int seeds = 0;
                    for (int index = 0; index < U16(death, 2); index++)
                    {
                        int at = 4 + index * 131;
                        if (U32(death, at + 6) == PandemoniumRegionalDropPolicy.BirthSeedId)
                            seeds += checked((int)U32(death, at + 10));
                    }
                    That(U16(death, 0) == key && seeds == 1, "真实死亡38通知包含一个完整诞生之芽，不伪造成功或仅改清单");
                    PickupResourceDrops(fixture, peer, death, 0, resources);
                    picked += seeds;
                    string saved = fixture.Snapshot();
                    peer.Send(39, report); peer.Barrier();
                    That(fixture.Snapshot() == saved, "重复死亡和拾取不重复发放地区材料");
                }
                That(picked == 2 && Amount(peer.Id) == picked, "两只真实地图普通怪的芽分别生成并在拾取后精确入库");
            }
            using (var peer = Open(fixture))
            {
                peer.Select(character.Slot, prepared: true, resumeTown: true, room: false);
                That(Amount(peer.Id) == picked, "地区材料断线重登后保留，不依赖临时地面或在线快照");
            }
            Console.WriteLine($"PASS regional socket dungeon={dungeonId} seeds={picked}; isolated material multiplier=10");

            long Amount(ushort id) => Convert.ToInt64(fixture.Query(
                "SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1",
                id, PandemoniumRegionalDropPolicy.BirthSeedId)[0][0]);
        }
    }
}
