using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class MooncreekDungeonTcpTests
{
    internal static void Verify(TcpTestFixture fixture,TcpTestResources resources)
    {
        var growth = SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
        var position = resources.Towns.Spawn(new Location(18,0,0,0)) ?? throw new InvalidDataException("月轮山入口缺少可站立区域");
        foreach (var sample in new[] { (Id:3200,Map:38115),(Id:3201,Map:38120),(Id:3202,Map:38125) })
        {
            string account = "mooncreek-"+sample.Id;
            Character created;
            using (var owner = new AccountOwners().Acquire(account,() => { }))
            {
                var candidate = CharacterCreation.Prepare("Moon"+sample.Id,resources.Characters.Get(0),position,position,resources.Learning.Get);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                {
                    Level=84,AcceptedStoryLevel=84,
                    InitialLearning=SkillPointLedger.Synchronize(0,0,84,candidate.InitialLearning with
                        { Experience=growth.GetLevelStartExperience(84) },resources.Learning)
                });
            }
            // 疲劳通知保持已用+剩余=上限，夹具改余额时同步回填已用量。
            int initialFatigue = sample.Id == 3201 ? 16 : 8;
            fixture.Sql("UPDATE character_fatigue SET used=maximum-$p1,remaining=$p1 WHERE character_id=$p0",created.Id,initialFatigue);
            using var peer = Open(fixture,account);
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),1]); peer.Expect(0,27,peer.SelectionNoticeBody());
            byte[] selection = Convert.FromHexString("030000000000000000FFFF0000000000000000000000000000");
            BinaryPrimitives.WriteInt32LittleEndian(selection,sample.Id);
            string before = fixture.Snapshot();
            foreach (byte difficulty in new byte[] { 0,2,3,4 })
            {
                selection[4]=difficulty; peer.Send(16,selection); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
                That(fixture.Snapshot()==before,"月轮山错误难度拒绝后不扣疲劳、预留实例或写角色");
            }
            selection[4]=1;
            peer.Send(16,selection);
            byte[] info = peer.Expect(0,28).Body;
            That(U32(info)==sample.Id && info[4]==1,"月轮山三图保留各自副本编号和资源固定难度1");
            byte[] map = peer.Expect(0,29).Body;
            That(U32(map,14)==sample.Map,"月轮山三图从各自真实起始地图进入");
            That(fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0",peer.Id) is [[long remaining]] &&
                remaining == initialFatigue - 8,
                "月轮山合法入场沿资源恰好扣除8点疲劳");
            FormalDungeonTcpTests.Load(peer); QuestLifecycleTcpTests.DrainNotifications(peer);
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
            peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "月轮山未击杀领主退出不伪造通关结算");
            if (sample.Id == 3201)
            {
                peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
                peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),1]); peer.Expect(0,27,peer.SelectionNoticeBody());
                peer.Send(16,selection);
                info = peer.Expect(0,28).Body;
                map = peer.Expect(0,29).Body;
                var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(sample.Id).Mazes[info[7]];
                var path = QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),new(info[8],info[9]));
                foreach (var point in path)
                {
                    if (point != path[0])
                    {
                        byte[] move = new byte[100]; move[0] = (byte)point.X; move[1] = (byte)point.Y;
                        peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                    }
                    FormalDungeonTcpTests.Load(peer);
                    QuestLifecycleTcpTests.DrainNotifications(peer);
                    foreach (int at in Enumerable.Range(0,map[18]).Select(index => 19 + 22 * index)
                        .Where(at => map[at + 21] == 0 && map[at + 13] != 9).OrderBy(at => map[at + 13] == 3))
                    {
                        ushort key = U16(map,at + 6);
                        peer.Send(39,FormalDungeonTcpTests.Death(key,peer.Id,map[at + 12]));
                        QuestLifecycleTcpTests.DrainNotifications(peer);
                    }
                }
                byte[] report = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99);
                peer.Send(46,report);
                var settled = new List<WirePacket>();
                QuestLifecycleTcpTests.DrainNotifications(peer,settled.Add);
                int rewardIndex = settled.FindIndex(packet => packet is { Kind:0,Opcode:35 });
                int cardIndex = settled.FindIndex(packet => packet is { Kind:0,Opcode:615 });
                int buffIndex = settled.FindIndex(packet => packet is { Kind:0,Opcode:487 });
                That(rewardIndex >= 0 && cardIndex == rewardIndex + 1 && buffIndex == cardIndex + 1 &&
                    settled[cardIndex].Body.Length == 5 && settled[cardIndex].Body[0] == 1 &&
                    U32(settled[cardIndex].Body,1) is >= 110 and <= 115 &&
                    settled[buffIndex].Body.SequenceEqual(DfoServer.Network.Builders.GuildEconomyPackets.AddedBuffs(
                        [new(0,U32(settled[cardIndex].Body,1),0)],0)),
                    "月轮山通关在35后紧接发送615月溪镇Buff翻牌及487角色Buff增益");
                uint rolledBuff = U32(settled[cardIndex].Body,1);
                peer.Send(46,report);
                var replay = new List<WirePacket>();
                QuestLifecycleTcpTests.DrainNotifications(peer,replay.Add);
                That(replay.Single(packet => packet is { Kind:0,Opcode:615 }).Body.SequenceEqual(settled[cardIndex].Body) &&
                    U32(replay.Single(packet => packet is { Kind:0,Opcode:615 }).Body,1) == rolledBuff,
                    "月轮山重复46回放保持同一冻结月溪镇Buff编号");
                peer.Send(72,[1,2]); peer.Expect(1,72,[1,1,2]);
                peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
                peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            }
        }
    }
}
