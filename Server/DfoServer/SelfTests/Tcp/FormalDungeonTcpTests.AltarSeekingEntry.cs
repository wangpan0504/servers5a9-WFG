using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunAltarSeekingEntryAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "altar-seeking-entry"), resources: resources);
        await fixture.StartAsync();
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var growth = SelfTestCatalogs.Growth(resources.Archive);
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 11006));
        foreach (ushort questId in new ushort[] { 5651, 5652, 5653 })
        {
            var quest = resources.Quests.Get(questId);
            That(quest.Grade == "[side]" && quest.Condition == "[seeking]" && quest.MinimumLevel == 60 &&
                quest.Definition!.Dungeons.Any(row => row.DungeonId == 11006) && quest.Definition.Drops.Length > 0,
                "祭坛收集外传保持真实资源与掉落声明");
            var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(area.Default.Bounds.SelectMany(row => row.Quests))
                .Concat(area.Default.DungeonGates.SelectMany(row => row.Quests)).Concat(new[] { 63, 5650, 5651, 5652 }.Where(id => id < questId)).Distinct().ToArray();
            var bounds = area.Map(done.ToHashSet()).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new DfoServer.GameWorld.Towns.Location(area.Town, area.Area,
                checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var repo = new CharacterRepository(fixture.Database);
                var candidate = CharacterCreation.Prepare("Seek" + questId, resources.Characters.Get(3), position, position, resources.Learning.Get);
                created = repo.Create(owner, candidate with { Level = 60, AcceptedStoryLevel = 60, CompletedQuests = done,
                    InitialLearning = SkillPointLedger.Synchronize(3, 0, 60, candidate.InitialLearning with
                        { Experience = growth.GetLevelStartExperience(60) }, resources.Learning) });
                created = repo.CommitInventory(owner, created, new(created.Inventory, created.Inventory with { Gold = 500000 }, "altar_seeking_seed"));
            }
            using var peer = Open(fixture);
            peer.Select(created.Slot, prepared: true, resumeTown: true, room: false);
            SelectTowerMap(peer);
            var request = Selection();
            BinaryPrimitives.WriteUInt32LittleEndian(request, 11006);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), questId);
            string before = fixture.Snapshot();
            peer.Send(16, request); That(peer.Expect(1, 16).Body[0] == 0, "非法祭坛请求明确拒绝"); peer.Barrier();
            That(fixture.Snapshot() == before, "未接取收集外传不能借任务号入场或扣费");
            peer.Send(31, [31, 0, (byte)questId, (byte)(questId >> 8)]);
            That(peer.Expect(1, 31).Body[0] == 1, "真实31接取祭坛收集外传");
            RefreshedQuests(peer);
            request[4] = 1;
            before = fixture.Snapshot();
            peer.Send(16, request); That(peer.Expect(1, 16).Body[0] == 0, "非法祭坛请求明确拒绝"); peer.Barrier();
            That(fixture.Snapshot() == before, "祭坛非法难度不扣费或修改接取");
            request[4] = 0;
            peer.Send(16, request);
            var info = peer.Expect(0, 212).Body;
            var map = peer.Expect(0, 211).Body;
            peer.Expect(0, 14);
            That(U32(info, 0) == 11006 && U32(map, 8) == U32(map, 2), "收集外传通过专用祭坛通知进入真实首房");
            peer.Barrier();
            Load(peer); peer.Barrier();
            That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id) is [[long gold]] &&
                gold == 500000 - dungeons.AltarAdmission.BloodFee(60, 0), "首房加载后仅扣真实资源入场费");
        }
    }
}
