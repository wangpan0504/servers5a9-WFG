using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    static void VerifyBorderTownWarp(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth)
    {
        const int dungeonId = 291100268;
        const string account = "border558";
        var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(dungeonId).Mazes[0];
        That(maze.WarpMaps.SequenceEqual(new DungeonWarpMap[] { new(new(1,2),new(3,0)),new(new(3,0),new(1,2)) }) &&
            maze.BossEntrance is null && maze.QuestConnectionTokens.Count == 0,"真实亡命杀镇普通迷宫声明两条有向传送");
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeonId && option.QuestId == -1));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).ToHashSet();
        done.UnionWith(area.Map(done).DungeonGates.Where(gate => gate.Conditions.Length == 0).SelectMany(gate => gate.Quests));
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        Character created;
        using (var owner = new AccountOwners().Acquire(account,() => { }))
        {
            var candidate = CharacterCreation.Prepare("BorderWarp",resources.Characters.Get(0),position,position,resources.Learning.Get);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
            {
                Level = 95,AcceptedStoryLevel = 95,CompletedQuests = done.ToArray(),
                InitialLearning = SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(95) },resources.Learning)
            });
        }
        using var peer = Open(fixture,account);
        peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
        SelectMap(peer, incompleteHellQuest: HasIncompleteHellQuest(resources, created), daybreakRemaining: 2);
        var selection = Selection();
        BinaryPrimitives.WriteUInt32LittleEndian(selection,dungeonId);
        BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16),0);
        peer.Send(16,selection);
        That(peer.Expect(0,28).Body[7] == maze.SourceOrdinal,"普通选图进入实际普通迷宫");
        byte[] map = peer.Expect(0,29).Body;
        Load(peer); Drain();
        var path = QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),new(1,2));
        foreach (var next in path.Skip(1)) { KillRoom(); Move(next,false); }
        That(U32(map,14) == 292103109,"沿普通相邻通路抵达Issue558的源房间");
        RejectMove(new(3,0));
        KillRoom();
        RejectMove(new(4,0));
        RejectMove(new(3,0),1);
        Move(new(3,0),false);
        That(U32(map,14) == 292104854,"精确有向传送发送ACK45与实际目标MAP");
        KillRoom();
        var cleared = State();
        Move(new(1,2),true);
        Move(new(3,0),true);
        That(State() == cleared && fixture.Query(
            "SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[0L]] &&
            fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
            "往返缓存不重生演员、不重复死亡成长或疲劳，也不伪造通关与任务凭据");
        peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
        peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();

        void KillRoom()
        {
            for (int index = 0; index < map[18]; index++)
            {
                int at = 19+22*index;
                if (map[at+13] == 9 || map[at+21] != 0) continue;
                peer.Send(39,Death(U16(map,at+6),peer.Id,map[at+12]));
                That(U16(peer.Expect(0,38).Body,0) == U16(map,at+6),"逐个提交本房实际演员死亡");
                Drain();
            }
        }
        void RejectMove(DungeonPoint point,byte kind = 0)
        {
            var before = State();
            peer.Send(45,Request(point,kind));
            peer.Expect(1,45,[0,19,0]);
            Drain();
            That(State() == before,"未清房、未声明目标及跨格kind1明确拒绝且不改库");
        }
        void Move(DungeonPoint point,bool cached)
        {
            peer.Send(45,Request(point,0)); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
            That(map[0] == point.X && map[1] == point.Y && map[2] == 0 &&
                (cached ? map.Length == 16 && map[13] == 0 : map.Length > 19 && map[13] == 1),
                "kind0沿原NOTI29首次完整创建、回访使用mode0缓存");
            Load(peer); Drain();
        }
        void Drain() => QuestLifecycleTcpTests.DrainNotifications(peer,packet =>
            That(packet.Opcode is not (29 or 31 or 115),"切图和普通清房不额外创建地图或提前发布结算许可"));
        byte[] Request(DungeonPoint point,byte kind)
        {
            var body = new byte[100]; body[0] = checked((byte)point.X); body[1] = checked((byte)point.Y); body[10] = kind;
            return body;
        }
        string State() => fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0",peer.Id)+
            fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id)+
            fixture.Snapshot("SELECT * FROM character_fatigue WHERE character_id=$p0",peer.Id);
    }
}
