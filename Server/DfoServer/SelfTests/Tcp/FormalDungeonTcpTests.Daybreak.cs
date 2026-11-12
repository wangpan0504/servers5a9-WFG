using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Network.Management;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunDaybreakAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new EntryCountClock();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "daybreak-entry"), resources: resources, clock: clock, gm: true);
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out _), "黎明验证真实疲劳扣除");
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 530));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(new[] { 6030, 6080, 6082, 10112 }).Distinct().ToArray();
        var bounds = area.Map(done.ToHashSet()).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, (ushort)(bounds.X + bounds.Width / 2), (ushort)(bounds.Y + bounds.Height / 2));
        var repository = new CharacterRepository(fixture.Database);
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        Character Create(string name, int passes, int buds, int passItemId = 490011739)
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(0), position, position, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(95) }, resources.Learning);
            var character = repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = done, InitialLearning = learning });
            var items = resources.ItemResources;
            var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
            var grants = new List<InventoryReward> { new(passItemId, passes) };
            if (buds > 0) grants.Add(new(10161610, buds));
            return InventoryService.Commit(repository, owner, character, inventory.Grant(character, grants));
        }
        var first = Create("Dawn900A", 3, 100);
        var second = Create("Dawn900B", 1, 0);
        var attendanceMixed = Create("Dawn900C", 3, 20, 490013045);
        var attendanceOnly = Create("Dawn900D", 1, 0, 490013045);
        byte[] request = Selection(3);
        BinaryPrimitives.WriteUInt32LittleEndian(request, 530);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), 0);
        void Enter(TcpPeer peer, byte remaining, bool usePass = true, int passItemId = 490011739)
        {
            SelectMap(peer, incompleteHellQuest: false, daybreakRemaining: remaining, assaultDay: (byte)clock.GetLocalNow().DayOfWeek);
            long CountItem(int item) => Convert.ToInt64(fixture.Query("SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1", peer.Id, item)[0][0]);
            string PassIdentity() => fixture.Snapshot("""
                SELECT i.item_uid,json_remove(i.item_core,'$.Value'),s.list_type,s.slot_index
                FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid
                WHERE i.character_id=$p0 AND json_extract(i.item_core,'$.ItemId')=$p1 ORDER BY i.item_uid
                """, peer.Id, passItemId);
            string Buds() => fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=10161610 ORDER BY item_uid", peer.Id);
            long buds = CountItem(10161610), passes = CountItem(passItemId);
            var passRows = fixture.Query("SELECT item_uid FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1", peer.Id, passItemId);
            That(passRows.Length == (usePass ? 1 : 0) && (usePass ? passes > 0 : passes == 0), "本轮扣券前核实际唯一券UID，无券回退不靠聚合掩盖残留实例");
            long passUid = usePass ? Convert.ToInt64(passRows[0][0]) : 0;
            string passIdentity = PassIdentity(), budsBefore = Buds();
            long fatigue = Convert.ToInt64(fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0", peer.Id)[0][0]);
            long attempt = Convert.ToInt64(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0]);
            long weeklyBefore = Convert.ToInt64(fixture.Query("SELECT coalesce(max(weekly_used),0) FROM character_dungeon_incount WHERE character_id=$p0 AND dungeon=530", peer.Id)[0][0]);
            peer.Send(16, request);
            peer.Expect(0, 28); peer.Expect(0, 29); peer.Expect(0, 14);
            var afterEntry = new byte[268];
            BinaryPrimitives.WriteUInt32LittleEndian(afterEntry, 2);
            BinaryPrimitives.WriteUInt32LittleEndian(afterEntry.AsSpan(4), 260);
            BinaryPrimitives.WriteUInt32LittleEndian(afterEntry.AsSpan(12), 530);
            BinaryPrimitives.WriteUInt32LittleEndian(afterEntry.AsSpan(16), checked((uint)(3 - remaining)));
            var countNotice = peer.Expect(0, 550).Body;
            That(countNotice.Length == 6 && countNotice[0] == 0x12 && countNotice[1] == 0x02 &&
                countNotice[2] == 0 && countNotice[3] == 0 && countNotice[4] == checked((byte)(remaining - 1)) &&
                countNotice[5] == checked((byte)(remaining - 1)),
                $"黎明入场550应为530日剩余/周剩余，实际={Convert.ToHexString(countNotice)}");
            peer.Expect(0, 1545, afterEntry);
            peer.Expect(0, 550, [0x12, 0x02, 0, 0, checked((byte)(remaining - 1)), 0]);
            Load(peer, fatigue: false); peer.Expect(0, 36); peer.Barrier();
            That(CountItem(10161610) == buds - (usePass ? 0 : 20) && CountItem(passItemId) == passes - (usePass ? 1 : 0) &&
                Convert.ToInt64(fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0", peer.Id)[0][0]) == fatigue - 8 &&
                Convert.ToInt64(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0]) == attempt + 1 &&
                fixture.Query("SELECT daily_used,weekly_used FROM character_dungeon_incount WHERE character_id=$p0 AND dungeon=530", peer.Id)
                    is [[long daily, long weekly]] && daily == 3 - remaining && weekly == weeklyBefore + 1,
                "黎明有券先扣一张、无券扣20芽，每次正常扣次数及8疲劳");
            That((!usePass || Buds() == budsBefore) && (usePass && passes == 1
                    ? fixture.Query("SELECT item_uid FROM character_inventory_items WHERE item_uid=$p0", passUid).Length == 0 &&
                      fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE item_uid=$p0", passUid).Length == 0
                    : PassIdentity() == passIdentity),
                "实际所选券保留原UID/核心/槽位只减一，最后一张完整移除，抵料时芽实例完全不变");
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0, 3); peer.Expect(0, 24);
            RefreshedQuests(peer); peer.Barrier();
        }
        void Reject(TcpPeer peer, byte remaining)
        {
            SelectMap(peer, incompleteHellQuest: false, daybreakRemaining: remaining, assaultDay: (byte)clock.GetLocalNow().DayOfWeek);
            string State() => string.Join("\n", new[] { "characters", "character_inventory_items", "character_inventory_slots", "character_fatigue", "character_dungeon_incount", "character_dungeon_state" }
                .Select(table => fixture.Snapshot($"SELECT * FROM {table} ORDER BY 1,2")));
            string before = State();
            for (int repeat = 0; repeat < 2; repeat++)
            {
                peer.Send(16, request); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
                That(State() == before, "黎明当日第三次及重复请求拒绝，不扣材料疲劳或分配实例");
            }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(first.Slot, prepared: true, resumeTown: true, room: false);
            Enter(peer, 2); Enter(peer, 1); Reject(peer, 0);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(first.Slot, prepared: true, resumeTown: true, room: false);
            Reject(peer, 0);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(second.Slot, prepared: true, resumeTown: true, room: false);
            Enter(peer, 2);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(attendanceMixed.Slot, prepared: true, resumeTown: true, room: false);
            Enter(peer, 2, passItemId: 490013045); Enter(peer, 1, passItemId: 490013045); Reject(peer, 0);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(attendanceMixed.Slot, prepared: true, resumeTown: true, room: false);
            Reject(peer, 0);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(attendanceOnly.Slot, prepared: true, resumeTown: true, room: false);
            Enter(peer, 2, passItemId: 490013045);
        }
        clock.Now = clock.Now.AddSeconds(2);
        using (var peer = Open(fixture))
        {
            peer.Select(first.Slot, prepared: true, resumeTown: true, room: false);
            Enter(peer, 2);
            Enter(peer, 1, usePass: false);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(attendanceMixed.Slot, prepared: true, resumeTown: true, room: false);
            Enter(peer, 2, passItemId: 490013045);
            Enter(peer, 1, usePass: false, passItemId: 490013045);
        }
    }
}
