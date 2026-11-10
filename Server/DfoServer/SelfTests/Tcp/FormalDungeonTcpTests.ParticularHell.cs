using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunParticularHellAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new EntryCountClock();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "particular-hell"), resources: resources, clock: clock);
        await fixture.StartAsync();
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var definition = dungeons.Get(316);
        That(definition.RequiredItem is { Count: > 0, Flag: not 0 }, "魔界真实资源有消耗型入场材料");
        var required = definition.RequiredItem!;
        var (done, position) = DungeonEntryCostTests.Selection(resources.Towns, 316);
        done.UnionWith(resources.Quests.All.Select(quest => quest.Id));
        int[] storyBooks = resources.Quests.StoryBookQuestIds.Where(done.Contains).Order().ToArray();
        byte difficulty = definition.TryGetDesignatedDifficulty(out byte designated) ? designated :
            (byte)Enumerable.Range(0, 5).First(value => definition.AllowsDifficulty((byte)value, false));
        var repository = new CharacterRepository(fixture.Database, resources.Quests);
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        Character Create(string name)
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(0), position, position, resources.Learning.Get);
            var character = repository.Create(owner, candidate with
            {
                Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [.. done],
                InitialLearning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(95) }, resources.Learning)
            });
            var items = resources.ItemResources;
            var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
            return InventoryService.Commit(repository, owner, character, inventory.Grant(character, [new(required.ItemId, checked(required.Count * 7))]));
        }
        var first = Create("Rift316A");
        var second = Create("Rift316B");
        byte[] request = Selection(difficulty);
        BinaryPrimitives.WriteUInt32LittleEndian(request, 316);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), 0);
        byte[] information = [], map = [];
        using (var peer = Open(fixture))
        {
            peer.Select(first.Slot, prepared: true, resumeTown: true, room: false);
            That(peer.ParticularHellUsed == 0, "首次选角832/833显示当前A角色3/3");
            for (uint used = 1; used <= 3; used++)
            {
                SelectMap(peer, incompleteHellQuest: false);
                That(peer.ParticularHellUsed == used - 1, "打开魔界选图重放当前角色真实已用次数");
                Enter(peer, used, checked((int)(7 - used)));
                peer.Send(16, request); peer.Barrier();
                That(Used(first.Id) == used, "加载中重复16不重扣次数");
                Load(peer);
                peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0, 3); peer.Expect(0, 24);
                RefreshedQuests(peer);
                // 全任务完成夹具包含故事簿；返城21/356后须显式核验703，不交给通用屏障跳过。
                peer.Expect(0, 703);
                That(peer.CompletedStoryBooks.SequenceEqual(storyBooks), "316撤退回城703严格恢复当前角色已完成的故事簿章节");
                peer.Barrier();
            }
            SelectMap(peer, incompleteHellQuest: false);
            string before = State();
            peer.Send(16, request); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
            That(State() == before && peer.ParticularHellUsed == 3 && Materials(first.Id) == required.Count * 4,
                "有材料的第四次真实TCP入场拒绝，角色、次数、材料、疲劳和尝试号不变化");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(second.Slot, prepared: true, resumeTown: true, room: false);
            That(peer.ParticularHellUsed == 0, "同账号B选角重新清表并显示独立3/3");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(first.Slot, prepared: true, resumeTown: true, room: false);
            That(peer.ParticularHellUsed == 3, "重连A从持久账本恢复0/3");
        }
        clock.Now = clock.Now.AddSeconds(2);
        using (var peer = Open(fixture))
        {
            peer.Select(first.Slot, prepared: true, resumeTown: true, room: false);
            That(peer.ParticularHellUsed == 0, "跨UTC+8 06:00重连A恢复3/3，非清空同账号所有角色");
            SelectMap(peer, incompleteHellQuest: false);
            Enter(peer, 1, 3);
            Load(peer);
            // 保留足量真实材料，完整走单人F10成功和次数耗尽回城。
            for (uint used = 1; used <= 3; used++)
            {
                Finish(peer);
                if (used < 3)
                {
                    peer.Send(72, [1, 0]);
                    var packets = PartyDungeonTcpTests.Drain(peer);
                    That(packets.Any(packet => packet is { Kind: 1, Opcode: 72, Body: [1, 1, 0] }) &&
                        packets.Any(packet => packet is { Kind: 0, Opcode: 14 }) && Used(first.Id) == used + 1,
                        "316单人F10确认成功并真实刷新材料及已提交次数");
                    information = packets.Single(packet => packet is { Kind: 0, Opcode: 28 }).Body;
                    map = packets.Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
                    ValidateCounts(peer, packets);
                    Load(peer);
                }
                else
                {
                    long materials = Materials(first.Id);
                    peer.Send(72, [1, 0]);
                    var packets = PartyDungeonTcpTests.Drain(peer);
                    That(packets.Any(packet => packet is { Kind: 1, Opcode: 72, Body: [1, 1, 2] }) &&
                        packets.Any(packet => packet is { Kind: 0, Opcode: 24 }) &&
                        packets.All(packet => packet.Opcode is not (28 or 29)) && Used(first.Id) == 3 &&
                        materials == required.Count && Materials(first.Id) == materials,
                        "316单人F10次数耗尽明确回城，不挂结果页或建立第4个实例");
                    ValidateCounts(peer, packets);
                }
            }
        }
        PartyDungeonTcpTests.VerifyParticularHell(fixture, resources, clock.Now);

        void Enter(TcpPeer peer, uint used, int materialPortions)
        {
            peer.Send(16, request);
            information = peer.Expect(0, 28).Body; map = peer.Expect(0, 29).Body; peer.Expect(0, 14);
            peer.ReadParticularHellCounts(); peer.Barrier();
            That(peer.ParticularHellUsed == used && Used(peer.Id) == used &&
                Materials(peer.Id) == required.Count * materialPortions,
                "832/833在正式入场扣料后发布，与当前角色真实已提交账本和材料一致");
        }
        void ValidateCounts(TcpPeer peer, WirePacket[] packets)
        {
            var pair = packets.Where(packet => packet.Opcode is 832 or 833).ToArray();
            That(pair is [{ Kind: 0, Opcode: 832, Body.Length: 264 }, { Kind: 0, Opcode: 833, Body.Length: 8 }],
                "316单人F10按832再833成对重放当前角色真实次数");
            foreach (var packet in pair) peer.ValidateServiceDayNotice(packet);
        }
        void Finish(TcpPeer peer)
        {
            var maze = definition.Mazes.Single(row => row.SourceOrdinal == information[7]);
            var route = QuestStoryRouteTests.Path(maze.StaticTopology, new(map[0], map[1]), new(information[8], information[9]));
            for (int index = 0; index < route.Length; index++)
            {
                foreach (int offset in Enumerable.Range(0, map[18]).Select(value => 19 + value * 22)
                    .Where(offset => map[offset + 21] == 0 && map[offset + 13] != 9))
                {
                    ushort key = U16(map, offset + 6);
                    peer.Send(39, Death(key, peer.Id, map[offset + 12]));
                    That(PartyDungeonTcpTests.Drain(peer).Any(packet => packet is { Kind: 0, Opcode: 38 } && U16(packet.Body) == key),
                        "316单人F10前逐房真实击杀提交");
                }
                if (index + 1 < route.Length)
                {
                    var move = new byte[100]; move[0] = (byte)route[index + 1].X; move[1] = (byte)route[index + 1].Y;
                    peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body; Load(peer);
                }
            }
            peer.Send(46, DungeonSettlementProtocolTests.Report(peer.Id, 99));
            That(PartyDungeonTcpTests.Drain(peer).Any(packet => packet is { Kind: 0, Opcode: 34 }),
                "316单人真实通关结算后才发F10");
        }
        long Used(ushort id) => Convert.ToInt64(fixture.Query(
            "SELECT daily_used FROM character_dungeon_incount WHERE character_id=$p0 AND dungeon=316", id).Single()[0]);
        long Materials(ushort id) => Convert.ToInt64(fixture.Query(
            "SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1",
            id, required.ItemId).Single()[0]);
        string State() => string.Join("\n", new[] { "characters", "character_inventory_items", "character_fatigue", "character_dungeon_incount", "character_dungeon_state" }
            .Select(table => fixture.Snapshot($"SELECT * FROM {table} ORDER BY 1,2")));
    }
}
