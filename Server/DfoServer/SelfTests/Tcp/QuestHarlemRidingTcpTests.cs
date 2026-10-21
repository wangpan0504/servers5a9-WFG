using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestHarlemRidingTcpTests
{
    // 真实5934资源、生产Handler与SQLite；316/45是独立编码的合成输入，不代表Windows自然演出。
    internal static void Verify(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain)
    {
        var quest = resources.Quests.Get(5934);
        var entrance = MiddleQuestEntryTests.Entrance(resources.Towns,quest,291100318);
        byte level = checked((byte)quest.MinimumLevel);
        foreach (bool ordinary in new[] { false,true })
        {
            string account = ordinary ? "harlem0" : "harlem5934";
            Character created;
            using (var owner = new AccountOwners().Acquire(account,() => { }))
            {
                var candidate = CharacterCreation.Prepare(ordinary ? "HarlemZero" : "HarlemRide",resources.Characters.Get(0),
                    entrance.Position,entrance.Position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,level,candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(level) },resources.Learning);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                    { Level = level,AcceptedStoryLevel = level,CompletedQuests = entrance.Completed,InitialLearning = learning });
            }
            using var peer = Open(fixture,account);
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            byte[] map = FormalDungeonTcpTests.Enter(peer,5934,291100318,
                requestQuestId:ordinary ? (ushort)0 : (ushort)5934);
            That(U32(map,14) == 292104867 && map[18] == 4,"哈林TCP起点保留四个已有骑乘演员");
            int navigation = 0;
            FormalDungeonTcpTests.Load(peer); Drain();
            string before = State();
            Move(2,0,expect:false);
            peer.Send(316,Operate(2,107010392,255,255)); Drain(); Move(2,0,expect:false);
            peer.Send(316,Operate(2,107002770)); Drain(); Move(2,0,expect:false);
            peer.Send(316,Operate(2,107010392).Concat(new byte[4]).ToArray()); Drain(); Move(2,0,expect:false);
            That(navigation == 0 && State() == before,"无绑定、哨兵上下文、敌方模板及额外CRC均不能换图或改库");

            peer.Send(316,Operate(2,107010392)); Drain();
            peer.Send(316,Operate(2,107010392)); Drain();
            Move(2,1,expect:false);
            peer.Send(316,Operate(1,107010392)); Drain(); Move(2,0,expect:false);
            peer.Send(316,Operate(2,107012119)); Drain();
            peer.Send(316,Operate(3,107012119)); Drain(); Move(2,0,expect:false);
            That(navigation == 0 && State() == before,"316静默绑定及下车/销毁不伪造确认、地图、死亡或奖励");

            peer.Send(316,Operate(2,107010392)); Drain();
            Move(2,0,expect:true);
            That(map[0] == 2 && map[1] == 0 && map[2] == 0 && U32(map,14) == 292104869 && map[18] == 2 &&
                U32(map,27) == 107002770 && U32(map,49) == 15442,
                "骑乘有向warp发ACK45及完整终末MAP，保留敌人与友方APC");
            FormalDungeonTcpTests.Load(peer); Drain();
            int moved = navigation;
            peer.Send(316,Operate(2,107010392)); Drain(); Move(0,0,expect:false);
            That(navigation == moved && fixture.Query(
                "SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0 AND quest_id=5934",peer.Id) is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "旧房316及逆向warp无效，加载终末不伪造通关与成长");
            peer.Send(34,FinishBody()); peer.Expect(1,34,[0,19,0]); Drain();
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
            peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();

            // 同一角色新Run不能沿用刚才的骑乘绑定。
            map = FormalDungeonTcpTests.Enter(peer,5934,291100318,requestQuestId:ordinary ? (ushort)0 : (ushort)5934);
            FormalDungeonTcpTests.Load(peer); Drain(); moved = navigation;
            Move(2,0,expect:false);
            That(navigation == moved,"重新入场创建新实例，不继承旧骑乘许可");
            peer.Send(316,Operate(2,107010392)); Drain();
            peer.Send(40,[0,0,0,0]); Drain();
            Move(2,0,expect:false);
            That(navigation == moved,"角色死亡解除骑乘，不能凭旧绑定换图");

            void Move(byte x,byte kind,bool expect)
            {
                var body = new byte[100]; body[0] = x; body[10] = kind;
                peer.Send(45,body);
                if (expect)
                {
                    peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                }
                Drain();
            }
            void Drain() => drain(peer,packet =>
            {
                That(packet.Opcode is not (296 or 300 or 316),"不发送未经证明的骑乘确认或重复创建通知");
                That(packet.Opcode != 31,"未报告终末死亡不产生结算许可");
                if (packet.Opcode is 29 or 45) navigation++;
            });
            byte[] FinishBody() => [34,0,46,23,255,255,1,0,255,255];
            string State() => fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0",peer.Id)+
                fixture.Snapshot("SELECT * FROM character_quest_clear_receipts WHERE character_id=$p0",peer.Id)+
                fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id);
        }
    }

    static byte[] Operate(byte operation,uint resource,byte x = 0,byte y = 0)
    {
        var body = new byte[7]; body[0] = operation;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1),resource);
        body[5] = x; body[6] = y;
        return body;
    }
}
