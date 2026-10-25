using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class QuestPetRewardTcpTests
{
    public static async Task RunAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        // 真实2675仍受专用角色限制；此合成会面只验证相同原奖励的共享网络合同。
        const ushort id=29969;
        var quest=QuestEligibilityTests.Quest(id) with { Npc=400001083,CompleteNpc=-1,IntData=[400001083],
            Attributes=["not give exp quest"],Rewards=[new(0,100331543,null),new(0,1,null),new(0,63493,null),new(0,1,null)] };
        var catalog=new QuestCatalog(new Dictionary<int,StoryQuest>{[id]=quest});
        catalog=TcpSyntheticQuestCatalog.WithHopeMissions(catalog,resources.Quests);
        await using var f=new TcpTestFixture(source,Path.Combine(root,"quest-pet-reward"),resources:resources,questCatalog:catalog);
        var repo=new CharacterRepository(f.Database,catalog);
        using(var owner=new AccountOwners().Acquire(f.Options.DevelopmentAccount,()=>{}))
            repo.Create(owner,CharacterCreation.Prepare("QuestPet",resources.Characters.Get(0),resources.Towns.Room,
                resources.Towns.InitialReturn,resources.Learning.Get));
        await f.StartAsync();
        long uid;
        using(var peer=Open(f))
        {
            peer.Select();
            peer.Send(31,Request(31,[])); peer.Expect(1,31,[1,(byte)(id&255),(byte)(id>>8),1,0,0,0,0]); Drain(peer);
            peer.Send(33,Request(33,[0,0])); peer.Expect(1,33,[1,(byte)(id&255),(byte)(id>>8),0,0,0,0]); Drain(peer);
            peer.Send(34,Request(34,[255,255,1,0,255,255]));
            byte[] detail=peer.Expect(0,105).Body;
            That(detail[0]==1,"ACK34之前先发布一只真实宠物的详情");
            uid=Convert.ToInt64(f.Query("SELECT i.item_uid FROM character_inventory_items i JOIN inventory_creature_details d ON d.item_uid=i.item_uid")[0][0]);
            That(U32(detail,1)==uid,"详情表使用已提交UID");
            byte[] ack=peer.Expect(1,34).Body;
            That(ack.Length==48 && ack[0]==1 && U16(ack,1)==id && ack[3]==4 && ack[8]==0 && ack[9]==2,
                "混合称号与宠物完整ACK34含两个19B项");
            That(U32(ack,12)==100331543 && U16(ack,29)==0 && U32(ack,31)==63493 && U32(ack,35)==uid,
                "宠物奖励携带Pet槽0、模板63493与真实UID，称号仍在Main");
            var main=peer.Expect(0,14).Body; var pet=peer.Expect(0,14).Body;
            That(main[0]==0 && pet[0]==7 && U16(main,1)==1 && U16(pet,1)==1 && U16(pet,3)==0 && U32(pet,5)==63493 && U32(pet,9)==uid,
                "ACK后按真实列表分别校准Main与Pet，不把两个列表混装");
            Drain(peer);
            string before=f.Snapshot();
            peer.Send(34,Request(34,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); peer.Barrier();
            That(f.Snapshot()==before,"同连接重复交付不重发详情和奖励");
            peer.Send(3,[0]); peer.Expect(1,3,[1]); peer.Closed();
        }
        using(var peer=Open(f))
        {
            peer.Select(prepared:true);
            That(peer.CompletedQuestHistory[id+4]==1 && f.Query("SELECT count(*) FROM inventory_creature_details") is [[1L]],
                "重登保留完成位及唯一宠物详情");
            peer.Send(34,Request(34,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); peer.Barrier();
            That(f.Query("SELECT item_uid FROM inventory_creature_details") is [[long saved]] && saved==uid &&
                f.Query("SELECT count(*) FROM character_quest_completion_receipts") is [[1L]],"重登重放仍为原UID与唯一回执");
        }
        That(f.Query("PRAGMA foreign_key_check").Length==0,"跨域奖励外键完整");
        static byte[] Request(byte op,byte[] tail)=>[op,0,(byte)(id&255),(byte)(id>>8),..tail];
        static void Drain(TcpPeer peer)=>QuestLifecycleTcpTests.DrainNotifications(peer);
    }
}
