using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    internal static async Task RunTrainQuestAsync(ServerOptions options,string root,TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options,Path.Combine(root,"train-quest-terminal"),resources:resources);
        await fixture.StartAsync();
        var growth = SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
        var dungeon = SelfTestCatalogs.Dungeons(resources.Archive).Get(91);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var maze = dungeon.Mazes.Single(row => row.ConnectsQuest(3581));
        var entrance = MiddleQuestEntryTests.Entrance(resources.Towns,resources.Quests.Get(3581),91);
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
        {
            var candidate = CharacterCreation.Prepare("TrainEnd",resources.Characters.Get(0),entrance.Position,entrance.Position,resources.Learning.Get);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
            {
                Level=77,AcceptedStoryLevel=77,CompletedQuests=entrance.Completed,
                InitialLearning=SkillPointLedger.Synchronize(0,0,77,candidate.InitialLearning with
                    { Experience=growth.GetLevelStartExperience(77) },resources.Learning)
            });
        }
        using var peer = Open(fixture);
        peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
        uint next = checked((uint)(long)fixture.Query("SELECT next_object_key FROM character_dungeon_state WHERE character_id=$p0",peer.Id)[0][0]);
        var plans = new List<DungeonMonsterPlan>();
        foreach (var map in maze.MapSpecification!.Concat(maze.LayeredMaps))
        {
            var plan = DungeonMonsterPlan.Create(rooms,map,peer.Id,checked((ushort)next),dungeon.BasisLevel,0);
            plans.Add(plan);
            if (plan.Monsters.Count != 0 || plan.SpecialObjects.Count != 0) next = plan.NextObjectKey;
        }
        byte[] current = Enter(peer,3581,91,incompleteHellQuest:HasIncompleteHellQuest(resources,created));
        int enabled=0,checks=0;
        var path = QuestStoryRouteTests.Path(maze.StaticTopology,maze.Starts.Single(),maze.Bosses.Single());
        foreach (var point in path)
        {
            var plan = plans.Single(row => row.Room.Map.Kind != "layered" && row.Room.Map.Position == point);
            VerifyMap(plan); Load(peer); Drain();
            if (point == maze.Bosses.Single()) break;
            foreach (var monster in plan.Monsters.Where(row => row.IsBlocking))
            {
                peer.Send(39,Death(monster.ObjectKey,peer.Id,checked((byte)monster.CandidateLevel))); Drain();
            }
            current = Move(path[Array.IndexOf(path,point)+1],0);
        }
        That(U32(current,14) == 70077 && enabled == 0 && checks == 0,"TCP真实基础战斗图不提前许可结算");
        current = Move(new(4,0),1);
        var terminal = plans.Single(row => row.Room.Map.Id == 70078);
        VerifyMap(terminal); Load(peer); Drain();
        That(enabled == 0 && checks == 0,"TCP站台加载不冒充终末死亡");
        var boss = terminal.Monsters.Single(row => row.MonsterId == 70136);
        byte[] death = new byte[74]; BinaryPrimitives.WriteUInt32LittleEndian(death,boss.ObjectKey);
        BinaryPrimitives.WriteUInt16LittleEndian(death.AsSpan(4),peer.Id); death[31]=0x22; SealDeath(death);
        peer.Send(39,death); Drain();
        That(enabled == 1 && checks == 1 && fixture.Query(
            "SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3581",peer.Id) is [[0L]] &&
            fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0 AND map_id=70078 AND monster_key=$p1",peer.Id,boss.ObjectKey) is [[1L]],
            "TCP末层真实39同时提交1/1并发布一次115和空31");
        string saved = fixture.Snapshot(); peer.Send(39,death); Drain();
        That(saved == fixture.Snapshot() && enabled == 1 && checks == 1,"TCP重复末层39不重复通知或资产");
        byte[] report = DungeonSettlementProtocolTests.Report(peer.Id,99);
        peer.Send(46,report); Drain();
        That(fixture.Query("SELECT map_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[70078L]],
            "TCP46以真实站台末层保存唯一结算");
        saved = fixture.Snapshot(); peer.Send(46,report); Drain();
        That(saved == fixture.Snapshot(),"TCP重复46不重复结算奖励");
        bool successor=false;
        peer.Send(34,[34,0,253,13,255,255,1,0,255,255]);
        var completed = peer.Expect(1,34).Body;
        That(completed.Length >= 10 && completed[0] == 1 && U16(completed,1) == 3581,"TCP34完成原猎杀接取");
        QuestLifecycleTcpTests.DrainNotifications(peer,packet =>
        {
            if (packet.Opcode == 21) successor = QuestLifecycleTcpTests.List(packet.Body,checked((byte)(long)fixture.Query(
                "SELECT level FROM characters WHERE id=$p0",peer.Id)[0][0])).Contains(3582);
        });
        That(resources.Quests.Get(3582).Prerequisites.Contains(3581) && successor && fixture.Query(
            "SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3581",peer.Id) is [[1L]],
            "TCP交付后21真实开放后续3582，不假造下一任务完成");
        peer.Send(42); peer.Expect(1,42,[1]);
        peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]); peer.Expect(0,24); Drain();
        Console.WriteLine("PASS 列车真实TCP 39/115/31、46幂等、34后继与42回城（合成客户端，非实机）");

        byte[] Move(DungeonPoint point,byte kind)
        {
            byte[] body=new byte[100]; body[0]=checked((byte)point.X); body[1]=checked((byte)point.Y); body[10]=kind;
            peer.Send(45,body); peer.Expect(1,45,[1]); return peer.Expect(0,29).Body;
        }
        void VerifyMap(DungeonMonsterPlan plan)
        {
            That(U32(current,14) == plan.Room.Map.Id && current[18] == plan.Monsters.Count+plan.SpecialObjects.Count,"TCP地图与完整原演员数一致");
            for (int i=0;i<plan.Monsters.Count;i++)
                That(U16(current,19+22*i+6) == plan.Monsters[i].ObjectKey && U32(current,19+22*i+8) == plan.Monsters[i].MonsterId,
                    "TCP独立核对对象key与模板，不能拿怪物ID冒充对象key");
        }
        void Drain() => QuestLifecycleTcpTests.DrainNotifications(peer,packet =>
        {
            if (packet.Opcode == 31) { Bytes(packet.Body,[],"列车结算许可为原生空31"); enabled++; }
            if (packet.Opcode == 115) checks++;
        });
    }
}
