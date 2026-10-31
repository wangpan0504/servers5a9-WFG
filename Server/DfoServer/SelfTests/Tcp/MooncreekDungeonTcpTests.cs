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
            fixture.Sql("UPDATE character_fatigue SET remaining=8 WHERE character_id=$p0",created.Id);
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
            That(fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0",peer.Id) is [[0L]],
                "月轮山合法入场沿资源恰好扣除8点疲劳");
            FormalDungeonTcpTests.Load(peer); QuestLifecycleTcpTests.DrainNotifications(peer);
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
            peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "月轮山未击杀领主退出不伪造通关结算");
        }
    }
}
