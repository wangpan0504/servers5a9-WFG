using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.PcRoom;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunFatigueAccelerationExperienceAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        FatigueAccelerationTests.RunCore();
        ExperienceBonusTests.RunCore();
        GrowthCubeContractTests.RunCore();
        DungeonSettlementProtocolTests.Run();
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "fatigue-experience"), gm: true, resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var candidate = CharacterCreation.Prepare("BurnStory", resources.Characters.Get(9), resources.Towns.Room,
            resources.Towns.InitialReturn, resources.Learning.Get);
        Character character;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            character = repository.Create(owner, candidate with
            {
                Level = 4, AcceptedStoryLevel = 1, CompletedQuests = [3145, 4873, 3157],
                InitialLearning = SkillPointLedger.Synchronize(9, 0, 4,
                    candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(4) }, resources.Learning)
            });
        That(character.Inventory.Items.All(item => resources.ItemResources.Items.Get(item.Core.ItemId).ExperiencePercent == 0 &&
            item.Core.Kind != DfoServer.GameWorld.Items.ItemKind.Avatar), "剧情燃烧夹具保留原初装且没有装备或时装经验加成");
        var pcRoom = new PcRoomCatalog(resources.Archive);
        var bonusRules = ExperienceBonusCatalog.Load(resources.Archive);
        That(pcRoom.AccelerationPercent == 100 && bonusRules.InternetCafePercent == 20,
            "本版资源独立声明燃烧100%及通关网吧20%，不合并或重复乘算");
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "pc-room on", out var reply) &&
            reply == "OK {\"premiumPcRoom\":true}", "隔离服务开启网吧资格");
        using var peer = Open(fixture);
        peer.Select(character.Slot, prepared: true, combos: StoredCombos(fixture, character.Id), premiumPcRoom: true);
        // 初始冒险团没有通关加成，显式准备资源中首个有加成的等级来复现基数差异。
        int preparedAdventureLevel = resources.Adventure.Benefits.OrderBy(row => row.Key)
            .First(row => row.Value.Growth.GetValueOrDefault("clear result exp up") > 0).Key;
        fixture.Sql("UPDATE account_adventures SET level=$p0,experience=0 WHERE account=$p1",
            preparedAdventureLevel, fixture.Options.DevelopmentAccount);
        peer.Send(562, [1]); peer.Expect(1, 562, [1]); peer.Expect(0, 490, [0, 0, 1]); peer.Barrier();
        That(fixture.Query("SELECT enabled FROM character_fatigue_acceleration WHERE character_id=$p0", character.Id) is [[1L]],
            "4级女鬼剑经真实562保存开启选择后才进入剧情Run");
        EntryTown(peer);
        byte[] information = [];
        byte[] map = Enter(peer, questId: 3146, information: body => information = body);
        var dungeon = SelfTestCatalogs.Dungeons(resources.Archive).Get(3);
        var maze = dungeon.Mazes.Single(row => row.SourceOrdinal == information[7]);
        var route = maze.MapSpecification!.OrderBy(row => row.Id).ToArray();
        That(maze.ConnectsQuest(3146) && route.Select(row => row.Id).SequenceEqual(Enumerable.Range(76131, 6)),
            "真实D3+3146选择其独立六房剧情路线，不能落到普通或3145路线");
        var kills = SelfTestCatalogs.KillExperience(resources.Archive);
        var weight = DungeonKillExperienceCatalog.GetDungeonWeight(dungeon);
        var named = DungeonKillExperienceCatalog.GetNamedMonsters(dungeon);
        ulong experience = character.Learning.Experience;
        uint monsterBurning = 0;
        int killed = 0, permits = 0;
        for (int room = 0; room < route.Length; room++)
        {
            That(U32(map, 14) == route[room].Id, "3146房间沿真实资源顺序进入");
            Load(peer, pcRoom: true);
            if (room == 0)
            {
                peer.Send(31, [31, 0, 238, 11]); peer.Expect(1, 31, [1, 238, 11, 1, 0, 0, 0, 0]);
                PartyDungeonTcpTests.Drain(peer);
            }
            // 领主死亡立即结束该剧情Run，先击杀其它敌人才能逐怪核对全部经验。
            foreach (int at in Enumerable.Range(0, map[18]).Select(row => 19 + row * 22).OrderBy(at => map[at + 13] == 3))
            {
                int monster = checked((int)U32(map, at + 8));
                if (map[at + 13] == 9 || monster == 107000906) continue;
                byte level = Convert.ToByte(fixture.Query("SELECT level FROM characters WHERE id=$p0", character.Id)[0][0]);
                byte monsterLevel = map[at + 12];
                uint basis = Game.Dungeons.DungeonKillExperiencePolicy.Calculate(kills.GetExperience(monsterLevel), weight,
                    kills.GetDifficultyRate(0), kills.GetMonsterRate(map[at + 13], named.Contains(monster)), level, monsterLevel);
                byte[] report = Death(U16(map, at + 6), character.Id, monsterLevel);
                peer.Send(39, report);
                var packets = PartyDungeonTcpTests.Drain(peer);
                var notice = packets.Single(packet => packet is { Kind: 0, Opcode: 37 }).Body;
                experience += checked(basis * 2);
                monsterBurning = checked(monsterBurning + basis);
                permits += packets.Count(packet => packet is { Kind: 0, Opcode: 31 });
                That(U64(notice, 1) == experience && U32(notice, 42) == basis && U32(notice, 34) == 0 && U32(notice, 87) == 0 &&
                    Experience(fixture, character.Id) == experience,
                    "剧情每次击杀实际到账基础加独立100%燃烧，37原生系统提示燃烧分项等于本怪基础经验");
                if (killed++ == 0)
                {
                    peer.Send(39, report);
                    That(!PartyDungeonTcpTests.Drain(peer).Any(packet => packet is { Kind: 0, Opcode: 37 }) &&
                        Experience(fixture, character.Id) == experience, "重复死亡不重复到账或累计杀怪燃烧");
                }
            }
            if (room + 1 < route.Length)
            {
                byte[] move = new byte[100];
                move[0] = checked((byte)route[room + 1].Position.X); move[1] = checked((byte)route[room + 1].Position.Y);
                peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
            }
        }
        That(killed > 0 && monsterBurning > 0 && permits == 1, "真实剧情击杀产生非零燃烧和唯一客户端结算许可");
        var clear = SelfTestCatalogs.ClearExperience(resources.Archive).Calculate(dungeon, 0, 99);
        int adventureLevel = Convert.ToInt32(fixture.Query("SELECT level FROM account_adventures WHERE account=$p0",
            fixture.Options.DevelopmentAccount)[0][0]);
        decimal adventurePercent = resources.Adventure.Benefits.TryGetValue(adventureLevel, out var benefit) &&
            benefit.Growth.TryGetValue("clear result exp up", out float percent) ? (decimal)percent : 0;
        uint adventure = checked((uint)decimal.Floor(clear.BaseExperience * adventurePercent / 100));
        That(adventure > 0, "通关燃烧夹具包含非零冒险团独立奖励，覆盖误合并基础和复乘的情况");
        uint pcRoomBonus = checked((uint)decimal.Floor(clear.BaseExperience * bonusRules.InternetCafePercent / 100));
        byte[] result = DungeonSettlementProtocolTests.Report(character.Id, 99);
        peer.Send(46, result);
        var settled = PartyDungeonTcpTests.Drain(peer);
        var clearNotice = settled.Single(packet => packet is { Kind: 0, Opcode: 37 }).Body;
        var cards = settled.Single(packet => packet is { Kind: 0, Opcode: 35 }).Body;
        experience += checked(clear.TotalExperience + adventure + pcRoomBonus + clear.BaseExperience);
        That(U64(clearNotice, 1) == experience && U32(clearNotice, 42) == clear.BaseExperience && U32(clearNotice, 46) == pcRoomBonus &&
            U32(cards, 0) == clear.BaseExperience && U32(cards, 41) == adventure && U32(cards, 29) == pcRoomBonus &&
            U32(cards, 53) == clear.BaseExperience && U32(cards, 77) == monsterBurning &&
            U32(cards, 85) == 0 && U32(cards, 93) == 0 && Experience(fixture, character.Id) == experience,
            "真实46冒险团单列41槽且只发一次，网吧20%与燃烧100%只取原基础，35与37分项和SQL到账一致");
        peer.Send(46, result);
        var repeated = PartyDungeonTcpTests.Drain(peer);
        That(Experience(fixture, character.Id) == experience && repeated.Where(packet => packet is { Kind: 0, Opcode: 35 })
            .All(packet => U32(packet.Body, 0) == clear.BaseExperience && U32(packet.Body, 41) == adventure &&
                U32(packet.Body, 53) == clear.BaseExperience && U32(packet.Body, 77) == monsterBurning),
            "重复结算恢复原两栏燃烧，不重新累计或发经验");
    }
}
