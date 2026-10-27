using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestAdventureSettlementTcpTests
{
    // 复用原隔离服务；39是明确合成输入，不代表客户端演出实发验收。
    internal static void Verify(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain)
    {
        VerifySummitEntry(fixture,resources,growth,drain);
        var quest = resources.Quests.Get(7930);
        var entrance = MiddleQuestEntryTests.Entrance(resources.Towns,quest,152);
        foreach (var scenario in new[] { (Request:7930u,Difficulty:(byte)1),(Request:0u,Difficulty:(byte)2) })
        {
            string account = "adventure-7930-"+scenario.Request;
            Character created;
            using (var owner = new AccountOwners().Acquire(account,() => { }))
            {
                var candidate = CharacterCreation.Prepare("Adventure"+scenario.Difficulty,resources.Characters.Get(0),entrance.Position,entrance.Position,resources.Learning.Get);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                {
                    Level = 40,AcceptedStoryLevel = 40,CompletedQuests = entrance.Completed,
                    InitialLearning = SkillPointLedger.Synchronize(0,0,40,candidate.InitialLearning with
                        { Experience = growth.GetLevelStartExperience(40) },resources.Learning)
                });
            }
            using (var peer = Open(fixture,account))
            {
                int permits = 0; ushort? deathKey = null;
                peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
                peer.Send(31,[31,0,250,30]); peer.Expect(1,31); Drain();
                peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
                peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),1]); peer.Expect(0,27,peer.SelectionNoticeBody());
                byte[] selection = Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000");
                BinaryPrimitives.WriteUInt32LittleEndian(selection,152); selection[4] = scenario.Difficulty;
                BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16),scenario.Request);
                peer.Send(16,selection); var information = peer.Expect(0,28).Body;
                That(U32(information,0) == 152 && information[4] == scenario.Difficulty,"7930 TCP保留真实副本及所选高难度");
                var map = peer.Expect(0,29).Body;
                for (byte x = 0; x < 4; x++)
                {
                    That(U32(map,14) == 655525u+x,"7930 TCP按实际四房路线进入");
                    FormalDungeonTcpTests.Load(peer); Drain();
                    That(permits == 0,"未发生终点真实死亡前不发送结算许可");
                    var rows = Enumerable.Range(0,map[18]).Select(index => 19+22*index).ToArray();
                    if (x < 3)
                    {
                        foreach (int offset in rows.Where(offset => map[offset+21] == 0 && map[offset+13] != 9))
                        { peer.Send(39,FormalDungeonTcpTests.Death(U16(map,offset+6),peer.Id,map[offset+12])); Drain(); }
                        var move = new byte[100]; move[0] = (byte)(x+1);
                        peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                    }
                    else
                    {
                        int friend = rows.Single(offset => map[offset+21] == 1 && map[offset+13] == 3);
                        int enemy = rows.Single(offset => map[offset+13] == 5);
                        ushort friendKey = U16(map,friend+6), enemyKey = U16(map,enemy+6);
                        peer.Send(39,FormalDungeonTcpTests.Death(friendKey,peer.Id,map[friend+12])); Drain();
                        That(permits == 0,"友方瓦格纳退场不伪造终点清图");
                        peer.Send(39,FormalDungeonTcpTests.Death(enemyKey,peer.Id,map[enemy+12])); Drain();
                        That(permits == 1 && deathKey == enemyKey,"真实普通敌对APC死亡后115引用其原key并发送一次31");
                        peer.Send(39,FormalDungeonTcpTests.Death(enemyKey,peer.Id,map[enemy+12])); Drain();
                        That(permits == 1,"重放死亡不重复许可或结算");
                    }
                }
                byte[] report = DungeonSettlementProtocolTests.Report(peer.Id,99);
                peer.Send(46,report); Drain();
                That(fixture.Query("SELECT map_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[655528L]],
                    "TCP46保存真实终点的一份结算回执");
                string before = fixture.Snapshot(); peer.Send(46,report); Drain();
                That(fixture.Snapshot() == before,"重复TCP46不重复发奖");
                peer.Send(34,[34,0,250,30,255,255,1,0,255,255]);
                That(peer.Expect(1,34).Body[0] == 1,"7930沿真实34正常交付"); Drain();
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
                peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();

                void Drain() => drain(peer,packet =>
                {
                    if (packet.Opcode == 31) { Bytes(packet.Body,[],"31保持空正文"); permits++; }
                    if (packet.Opcode == 115)
                    { That(packet.Body.Length == 4 && packet.Body[0] == 1 && packet.Body[1] == 1,"115使用原协议"); deathKey=U16(packet.Body,2); }
                });
            }
            using (var peer = Open(fixture,account))
            {
                peer.Select(created.Slot,prepared:true,resumeTown:true,room:false); peer.Barrier();
                That(fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=7930",peer.Id) is [[7930L]] &&
                    fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[1L]],
                    "回城重连保留已交付任务及唯一结算回执");
            }
        }
    }
    static void VerifySummitEntry(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain)
    {
        var entrance = MiddleQuestEntryTests.Entrance(resources.Towns,resources.Quests.Get(7975),9618);
        foreach (var scenario in new[] { (Request:7975u,Difficulty:(byte)0),(Request:0u,Difficulty:(byte)4) })
        {
            string account = "summit-7975-"+scenario.Request;
            Character created;
            using (var owner = new AccountOwners().Acquire(account,() => { }))
            {
                var candidate = CharacterCreation.Prepare("Summit"+scenario.Difficulty,resources.Characters.Get(0),
                    entrance.Position,entrance.Position,resources.Learning.Get);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                {
                    Level = 85,AcceptedStoryLevel = 85,CompletedQuests = entrance.Completed,
                    InitialLearning = SkillPointLedger.Synchronize(0,0,85,candidate.InitialLearning with
                        { Experience = growth.GetLevelStartExperience(85) },resources.Learning)
                });
            }
            using var peer = Open(fixture,account);
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            byte[] selection = Convert.FromHexString("922500000000000000FFFF0000000000271F00000000000000");
            selection[4] = scenario.Difficulty;
            BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16),scenario.Request);
            peer.Send(16,selection); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
            peer.Send(31,[31,0,39,31]); That(peer.Expect(1,31).Body[0] == 1,"7975 TCP真实接取成功"); drain(peer,null);
            peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),1]); peer.Expect(0,27,peer.SelectionNoticeBody());
            peer.Send(16,selection); var info = peer.Expect(0,28).Body;
            That(U32(info,0) == 9618 && info[4] == scenario.Difficulty,"山巅两入口保留副本与所选难度");
            var map = peer.Expect(0,29).Body;
            var definition = SelfTestCatalogs.Dungeons(resources.Archive).Get(9618);
            var maze = definition.Mazes.Single(row => row.SourceOrdinal == info[7]);
            for (byte x = 0; x < 3; x++)
            {
                That(U32(map,14) == 655961u+x,"山巅TCP沿实际路线到达原先拒绝的第三房");
                FormalDungeonTcpTests.Load(peer); drain(peer,null);
                if (x == 2)
                {
                    var source = DungeonMonsterPlan.Create(SelfTestCatalogs.Rooms(resources.Archive),
                        maze.MapSpecification!.Single(row => row.Id == 655963), peer.Id, 100, definition.BasisLevel, 0);
                    var observed = FormalDungeonTcpTests.ObserveChampionTypes(map, source, definition, scenario.Difficulty);
                    That(map[18] == 6 && observed.Monsters.Count == 6 && observed.Monsters.All(actor => actor.SpawnType <= 1),
                        "第三房完整生成六只普通来源怪，允许单只随机精英，不为两个空父行发送type9");
                    break;
                }
                foreach (int offset in Enumerable.Range(0,map[18]).Select(i => 19+22*i).Where(at => map[at+21] == 0))
                { peer.Send(39,FormalDungeonTcpTests.Death(U16(map,offset+6),peer.Id,map[offset+12])); drain(peer,null); }
                var move = new byte[100]; move[0] = (byte)(x+1);
                peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
            }
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
            peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "空父行兼容不伪造通关或结算回执");
        }
    }
}
