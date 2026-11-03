using System.Buffers.Binary;
using DfoServer.Development.Dungeons;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunContaminatedTimegateAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        ContaminatedTimegateEntryTests.RunResources(resources.Towns, resources.Archive, resources.Quests);
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "contaminated-timegate"), gm: true, resources: resources, sharedResourcePath: true);
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out _),
            "独立5100夹具关闭无限疲劳，核对普通0与显式任务原有疲劳区别");
        var area = resources.Towns.Areas[(12, 0)];
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(new[] { 3145, 4873, 3622, 5146 }).ToHashSet();
        done.UnionWith(area.Map(done).DungeonGates.First(row => row.Conditions.Length == 0).Quests);
        var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(12, 0, checked((ushort)(walk.X + walk.Width / 2)), checked((ushort)(walk.Y + walk.Height / 2)));
        That(area.DungeonWorldMap == 17 && resources.Towns.DestinationAdmission(position, 95, done) == "ready" &&
            resources.Towns.HasDungeonEntrance(area, done, 5100), "真实Town12/0以3622完成位和原地图门进入worldmap17");
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("TimeGateWire", resources.Characters.Get(0), position, position, resources.Learning.Get);
            created = new CharacterRepository(fixture.Database, resources.Quests).Create(owner, candidate with
            {
                Level = 95, AcceptedStoryLevel = 95, GrowType = 0x11, CompletedQuests = done.ToArray(),
                InitialLearning = SkillPointLedger.Synchronize(0, 0x11, 95, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning)
            });
        }
        using var peer = Open(fixture);
        peer.Select(created.Slot, prepared: true, resumeTown: true, room: false);
        That(peer.CompletedQuestHistory.Length == 30004 && peer.CompletedQuestHistory[4 + 3622] == 1 &&
            peer.CompletedQuestHistory[4 + 4929] == 0, "实际356解锁地图节点17，保留6/6尚未完成");
        SelectMap(peer, incompleteHellQuest: HasIncompleteHellQuest(resources, created));
        Reject(Request(0, 2), "仅完成3622尚无6/6活动任务时不能进入5100");
        peer.Send(132); peer.Expect(1, 132, [1]);
        peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]); peer.Expect(0, 23); peer.Expect(0, 24); peer.Barrier();
        peer.Send(31, [31, 0, 0x41, 0x13]);
        peer.Expect(1, 31, [1, 0x41, 0x13, 1, 0, 0, 0, 0]);
        RefreshedQuests(peer); peer.Barrier();
        string active = Active();
        That(fixture.Query("SELECT quest_id,condition_state,length(acceptance_id) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=4929", peer.Id)
            is [[4929L, 1L, 32L]], "真实31接取一觉剑魂6/6并生成唯一非空接取身份");
        long attempt = 0;
        foreach (uint requested in new uint[] { 0, 4929 })
        {
            SelectMap(peer, incompleteHellQuest: HasIncompleteHellQuest(resources, created));
            foreach (byte difficulty in new byte[] { 0, 1, 3, 4 })
                Reject(Request(requested, difficulty), "错误难度拒绝并保持同一选图及原接取");
            Reject(Request(4933, 2), "未接取的另一职业6/6不能冒用剑神入口");
            peer.Send(16, Request(requested, 2));
            var info = peer.Expect(0, 28).Body;
            var map = peer.Expect(0, 29).Body;
            That(info.Length == 36 && U32(info, 0) == 5100 && info[4] == 2 && info[8] == 3 && info[9] == 0 &&
                map.Length >= 19 && map[0] == 0 && map[1] == 0 && U32(map, 14) == 95261,
                "真实16两种任务号均按固定2加载5100原首房及Boss坐标，不走等级预约9583");
            Load(peer);
            if (requested == 0)
            {
                var fatigue = peer.Expect(0, 36).Body;
                var row = fixture.Query("SELECT used,maximum,remaining FROM character_fatigue WHERE character_id=$p0", peer.Id).Single();
                That(fatigue.Length == 10 && U16(fatigue, 0) == Convert.ToInt64(row[0]) &&
                    U16(fatigue, 2) == Convert.ToInt64(row[1]) && U16(fatigue, 6) == Convert.ToInt64(row[2]),
                    "普通0保持真实逐房疲劳合同，通知与SQLite一致");
            }
            peer.Barrier();
            That(Active() == active && fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id)
                is [[long actual]] && actual == ++attempt, "实际加载只预留一次尝试且不改6/6接取身份或条件");
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
            peer.Expect(0, 24); RefreshedQuests(peer); peer.Barrier();
            That(Active() == active && fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0", peer.Id) is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0", peer.Id) is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=4929", peer.Id) is [[0L]],
                "真实42未通关退出不制造清图、结算或二觉完成资格");
        }
        Console.WriteLine($"PASS {Count - checks} contaminated timegate real TCP 31/15/16/37/42 entry assertions (not client gameplay)");

        static byte[] Request(uint quest, byte difficulty)
        {
            var body = Selection(difficulty);
            BinaryPrimitives.WriteUInt32LittleEndian(body, 5100);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), quest);
            return body;
        }
        string Active() => fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot", peer.Id);
        string State() => fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", peer.Id) + Active() +
            fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", peer.Id) +
            fixture.Snapshot("SELECT * FROM character_fatigue WHERE character_id=$p0", peer.Id) +
            fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id);
        void Reject(byte[] body, string message)
        {
            string before = State();
            peer.Send(16, body); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
            That(State() == before, message);
        }
    }
}
