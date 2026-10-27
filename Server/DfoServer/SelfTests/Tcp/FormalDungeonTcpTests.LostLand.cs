using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunLostLandAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        LostLandSessionTests.Run(resources.Towns, resources.Archive, resources.Characters);
        int checks = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "lost-land"), resources: resources, clock: new AltarTimers(180));
        await fixture.StartAsync();
        var repository = new CharacterRepository(fixture.Database, resources.Quests);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var position = new Location(40, 0, 845, 155);
            for (byte slot = 0; slot < 3; slot++)
            {
                var candidate = CharacterCreation.Prepare("LostLandTcp" + slot, resources.Characters.Get(4), position, position, resources.Learning.Get);
                repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [3156],
                    InitialLearning = SkillPointLedger.Synchronize(4, 0, 95, candidate.InitialLearning with
                        { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning) });
            }
        }
        for (byte slot = 0; slot < 3; slot++)
        {
            using var peer = Open(fixture);
            peer.Select(slot, prepared: true, resumeTown: true, room: false);
            Enter(peer);
            if (slot == 0)
            {
                peer.Send(16, Request()); peer.Send(37, new byte[8]); peer.Barrier();
                That(Used() == 1, "真实TCP重复16/37不重扣次数或重发603");
            }
            if (slot < 2) peer.Expect(0, 31, []);
            if (slot == 0)
            {
                peer.Send(46, new byte[1]); peer.Barrier();
                That(Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts").Single().Single()) == 0,
                    "到期后普通46不授予普通结算或翻牌");
            }
            if (slot != 1) { peer.Send(42); peer.Expect(1, 42, [1]); }
            Town(peer);
            peer.Send(42); peer.Barrier();
            That(Used() == slot + 1, "自然结束、无42回城或主动退出均只消耗各角色一次，重复42与后续屏障正常");
        }
        Console.WriteLine($"PASS {Count - checks} 遗忘之地真实TCP603、到期31、42退出和无响应回城断言（非客户端验收）");

        void Enter(TcpPeer peer)
        {
            SelectMap(peer);
            peer.Send(16, Request());
            var information = peer.Expect(0, 28).Body;
            That(U32(information, 0) == 4000, "正式入场发送真实遗忘之地信息");
            var map = peer.Expect(0, 29).Body;
            That(U32(map, 14) == 11200, "正式起始房使用匹配PVF地图11200");
            peer.Expect(0, 550);
            Load(peer);
            peer.Expect(0, 603, [0x58, 0x02, 0, 0, 0xB0, 0x04, 0, 0]);
        }
        void Town(TcpPeer peer)
        {
            peer.Expect(0, 3);
            var transition = peer.Expect(0, 24).Body;
            That(transition[0] == 40 && transition[1] == 0, "退出落回乔安所在的原城镇");
            RefreshedQuests(peer); peer.Barrier();
        }
        long Used() => Convert.ToInt64(fixture.Query("SELECT sum(daily_used) FROM character_dungeon_incount WHERE dungeon=4000").Single().Single());
        static byte[] Request()
        {
            var body = Convert.FromHexString("A00F00000000000000FFFF0000000000000000000000000000");
            BinaryPrimitives.WriteUInt32LittleEndian(body, 4000);
            return body;
        }
    }
}
