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
    public static async Task RunEnterCountsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        var clock = new EntryCountClock();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "dungeon-enter-count"), resources: resources, clock: clock);
        await fixture.StartAsync();
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 11006));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(area.Default.Bounds.SelectMany(row => row.Quests))
            .Concat(area.Default.DungeonGates.SelectMany(row => row.Quests)).Distinct().ToArray();
        var bounds = area.Map(done.ToHashSet()).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new DfoServer.GameWorld.Towns.Location(area.Town, area.Area,
            checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        var growth = SelfTestCatalogs.Growth(resources.Archive);
        var repository = new CharacterRepository(fixture.Database, resources.Quests);
        Character first = Create("Entry694A"), second = Create("Entry694B");
        using (var peer = Open(fixture))
        {
            peer.Select(first.Slot, prepared: true, resumeTown: true, room: false);
            That(peer.DungeonEnterCounts[11006] == (3, 0) && peer.DungeonEnterCounts[11007] == (3, 0),
                "空账本选角即收到3/3，两玩法分别初始化，不等待先点一次副本");
            for (byte used = 1; used <= 3; used++)
            {
                Select(peer, checked((byte)(4 - used)));
                Enter(peer, checked((byte)(3 - used)));
                peer.Send(16, Request()); peer.Barrier();
                That(Entries(first.Id) == used, "加载阶段重复16不重复扣次，也不发送虚构次数通知");
                Load(peer); Return(peer);
            }
            Select(peer, 0);
            peer.Send(16, Request()); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
            That(Entries(first.Id) == 3, "当日第四次拒绝，数据库计数和最后一条550均保持零剩余");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(first.Slot, prepared: true, resumeTown: true, room: false);
            That(peer.DungeonEnterCounts[11007] == (0, 0), "重连从同一持久账本恢复用尽状态");
            Select(peer, 0);
            clock.Now = clock.Now.AddSeconds(2);
            peer.SendBarrier();
            var reset = peer.Read();
            // 每日视图可由连接计时器或本命令刷新，仍严格校验一条541和一条屏障应答。
            if (reset is { Kind: 0, Opcode: 541 }) peer.Expect(1, 1347, [1]);
            else
            {
                That(reset is { Kind: 1, Opcode: 1347, Body: [1] }, "跨日屏障之前只允许每日视图刷新");
                reset = peer.Expect(0, 541);
            }
            That(Inventory.DailyBoosterProjectionTests.Decode(reset.Body).Values.All(value => value == 0),
                "跨06:00校验既有每日礼盒清零通知，不吞掉其它包");
            peer.Barrier();
            Enter(peer, 2);
            Load(peer); Return(peer);
            That(Entries(first.Id) == 4, "06:00跨游戏日后原连接可入场，历史三次保留且本日只扣一次");
            Select(peer, 2);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(second.Slot, prepared: true, resumeTown: true, room: false);
            That(peer.DungeonEnterCounts[11007] == (3, 0) && peer.DungeonEnterCounts[11006] == (3, 0),
                "同账号换角色不继承上一角色的剩余次数，客户端条目重新覆盖");
            Select(peer, 3);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(first.Slot, prepared: true, resumeTown: true, room: false);
            That(peer.DungeonEnterCounts[11007] == (2, 0) && peer.DungeonEnterCounts[11006] == (3, 0),
                "换回原角色恢复本日2次，另一祭坛仍为3次");
        }
        // 无尽与免费极限共享次数通知，但费用只能取真实价格表，不能因显示3次跳过扣费。
        Character blood = Create("BloodFee");
        byte[] bloodRequest = Request();
        BinaryPrimitives.WriteUInt32LittleEndian(bloodRequest, 11006);
        using (var peer = Open(fixture))
        {
            peer.Select(blood.Slot, prepared: true, resumeTown: true, room: false);
            Select(peer, 3);
            for (int repeat = 0; repeat < 2; repeat++)
            {
                peer.Send(16, bloodRequest); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
            }
            That(Entries(blood.Id) == 0 && peer.DungeonEnterCounts[11006] == (3, 0),
                "无尽有3次但金币不足明确回复拒绝，重复请求不扣次数");
        }
        int fee = SelfTestCatalogs.Dungeons(resources.Archive).AltarAdmission.BloodFee(blood.Level, 0);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            blood = repository.LoadCharacter(owner, blood.Slot);
            blood = repository.CommitInventory(owner, blood, new(blood.Inventory, blood.Inventory with { Gold = checked((uint)fee) }, "blood_entry_fee"));
        }
        using (var peer = Open(fixture))
        {
            peer.Select(blood.Slot, prepared: true, resumeTown: true, room: false);
            Select(peer, 3);
            peer.Send(16, bloodRequest); peer.Expect(0, 212); peer.Expect(0, 211); peer.Expect(0, 14);
            Counts(peer, 3, blood: 2); peer.Barrier();
            peer.Send(16, bloodRequest); peer.Barrier();
            That(Entries(blood.Id) == 1 && fixture.Query("SELECT gold FROM characters WHERE id=$p0", blood.Id) is [[0L]],
                "补足真实费用后无尽入场，金币与次数只提交一次，加载重复不再扣款");
            Load(peer); Return(peer);
            Select(peer, 3, blood: 2);
            peer.Send(16, bloodRequest); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
            That(Entries(blood.Id) == 1 && fixture.Query("SELECT gold FROM characters WHERE id=$p0", blood.Id) is [[0L]],
                "选图550已确认剩余2次，费用不足继续拒绝且次数金币不变");
        }
        Console.WriteLine($"PASS {Count - before} 550真实TCP初始化、扣次、拒绝、重连、角色隔离与06:00重置断言（未启动客户端）");

        Character Create(string name)
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(0), position, position, resources.Learning.Get);
            return repository.Create(owner, candidate with
            {
                Level = 70, AcceptedStoryLevel = 70, CompletedQuests = done,
                InitialLearning = SkillPointLedger.Synchronize(0, 0, 70, candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(70) }, resources.Learning)
            });
        }
        long Entries(ushort id) => Convert.ToInt64(fixture.Query(
            "SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$p0", id).Single().Single());
        void Select(TcpPeer peer, byte ultimate, byte blood = 3)
        {
            peer.Send(15, new byte[4]); peer.Expect(1, 15, [1]); peer.Expect(0, 3);
            peer.Expect(0, 1536);
            peer.Expect(0, 1303, [0x34, 0]);
            Counts(peer, ultimate, blood);
            var notice = peer.Expect(0, 27).Body;
            That(notice.Length == 42, "550在27之前送达，原选图列表合同不变");
        }
        void Counts(TcpPeer peer, byte ultimate, byte blood = 3)
        {
            peer.Expect(0, 550, [0xFE, 0x2A, 0, 0, blood, 0]);
            peer.Expect(0, 550, [0xFF, 0x2A, 0, 0, ultimate, 0]);
            peer.ExpectGriefTowerEnterCounts();
            peer.ExpectDespairTowerEnterCounts();
            That(peer.DungeonEnterCounts[5115] == (1, 0), "两祭坛入场与跨日不消耗悲叹基础挑战");
            That(peer.DungeonEnterCounts[11008] == (1, 0), "两祭坛入场与跨日不消耗绝望基础挑战");
        }
        byte[] Request()
        {
            var request = Selection();
            BinaryPrimitives.WriteUInt32LittleEndian(request, 11007);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), 0);
            return request;
        }
        void Enter(TcpPeer peer, byte remaining)
        {
            peer.Send(16, Request()); peer.Expect(0, 212); peer.Expect(0, 211);
            Counts(peer, remaining);
            peer.Barrier();
        }
        void Return(TcpPeer peer)
        {
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0, 3); peer.Expect(0, 24);
            RefreshedQuests(peer); peer.Barrier();
        }
    }

    sealed class EntryCountClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 24, 5, 59, 59, TimeSpan.FromHours(8));
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
}
