using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class QuestAccountSynchronizationTcpTests
{
    public static async Task RunAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        await using var f=new TcpTestFixture(source,Path.Combine(root,"quest-account-sync"),resources:resources);
        const string account="quest-sync-tcp";
        var owners=new AccountOwners(); var repo=new CharacterRepository(f.Database,resources.Quests);
        var growth=SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
        var created=new List<Character>();
        using(var owner=owners.Acquire(account,()=>{}))
            foreach(string name in new[]{"SyncWireA","SyncWireB"})
            {
                var candidate=CharacterCreation.Prepare(name,resources.Characters.Get(0),resources.Towns.Room,
                    resources.Towns.InitialReturn,resources.Learning.Get);
                created.Add(repo.Create(owner,candidate with {Level=50,AcceptedStoryLevel=50,
                    InitialLearning=SkillPointLedger.Synchronize(0,0,50,candidate.InitialLearning with
                        {Experience=growth.GetLevelStartExperience(50)},resources.Learning)}));
            }
        await f.StartAsync();
        using(var peer=Open(f,account))
        {
            peer.Select();
            That(QuestLifecycleTcpTests.List(peer.AvailableQuests,50).Contains(8709),"首次资格下发真实账号会面任务");
            Accept(peer,8709); Meet(peer,8709); Finish(peer,8709);
            That(Gold(0)==created[0].Inventory.Gold+100 && Gold(1)==created[1].Inventory.Gold,
                "真实8709完成只给当前角色原100金币");
            Accept(peer,8710);
            string before=f.Snapshot("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=8710",created[0].Id);
            peer.Send(33,Request(33,8710,[0,0])); peer.Expect(1,33,[0,19,0]); Drain(peer);
            That(f.Snapshot("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=8710",created[0].Id)==before,
                "未到目标NPC不能把8710直接完成");
            peer.Travel(107,0,400,250,38,1,npc:400001081);
            Meet(peer,8710); Finish(peer,8710);
            peer.Travel(38,1,450,234,107,0,returnTown:38);
            That(Gold(0)==created[0].Inventory.Gold+200 && Gold(1)==created[1].Inventory.Gold,
                "真实两项分别发原100金币，往返不改变收益");
            peer.Send(3,[0]); peer.Expect(1,3,[1]); peer.Closed();
        }
        for(ushort slot=0;slot<2;slot++)
        {
            using var peer=Open(f,account); peer.Select(slot,prepared:true);
            foreach(int id in new[]{8709,8710})
            {
                That(peer.CompletedQuestHistory[id+4]==1,"换角色登录原生完成位同步两项账号任务");
                peer.Send(31,Request(31,id,[])); peer.Expect(1,31,[0,18,0]); Drain(peer);
                peer.Send(34,Request(34,id,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); Drain(peer);
            }
            peer.Send(3,[0]); peer.Expect(1,3,[1]); peer.Closed();
        }
        That(Gold(0)==created[0].Inventory.Gold+200 && Gold(1)==created[1].Inventory.Gold &&
            f.Query("SELECT quest_id FROM account_completed_quests WHERE account=$p0 ORDER BY quest_id",account) is [[8709L],[8710L]] &&
            f.Query("SELECT count(*) FROM character_quest_completion_receipts") is [[2L]],
            "重登及换角色重发不增加奖励或回执，账号按任务分别计账");
        await f.StopAsync();

        uint Gold(int slot)
        {
            using var owner=owners.Acquire(account,()=>{});
            return repo.LoadCharacter(owner,(ushort)slot).Inventory.Gold;
        }
    }
    static byte[] Request(byte opcode,int id,byte[] suffix) => [opcode,0,(byte)id,(byte)(id>>8),..suffix];
    static void Drain(TcpPeer peer) => QuestLifecycleTcpTests.DrainNotifications(peer);
    static void Accept(TcpPeer peer,int id)
    {
        peer.Send(31,Request(31,id,[])); var ack=peer.Expect(1,31).Body;
        That(ack[0]==1 && U16(ack,1)==id,"真实账号任务31接取成功"); Drain(peer);
    }
    static void Meet(TcpPeer peer,int id)
    {
        peer.Send(33,Request(33,id,[0,0])); peer.Expect(1,33,[1,(byte)id,(byte)(id>>8),0,0,0,0]); Drain(peer);
    }
    static void Finish(TcpPeer peer,int id)
    {
        peer.Send(34,Request(34,id,[255,255,1,0,255,255])); var ack=peer.Expect(1,34).Body;
        That(ack[0]==1,"真实账号任务34交付成功"); Drain(peer);
    }
}
