using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class QuestHellChallengeTcpTests
{
    static void VerifyAlbertOrphan(TcpTestFixture fixture,TcpTestResources resources)
    {
        var growth=SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
        var repository=new CharacterRepository(fixture.Database,resources.Quests);
        foreach(bool legacy in new[] {false,true})
        {
            string account=legacy ? "albert-legacy" : "albert-manual";
            Character created;
            using(var owner=new AccountOwners().Acquire(account,()=>{}))
            {
                var candidate=CharacterCreation.Prepare(legacy ? "AlbertOld" : "AlbertSkip",resources.Characters.Get(0),
                    resources.Towns.Room,resources.Towns.InitialReturn,resources.Learning.Get);
                // 末级历史主线档案，仅保留截图的父/子活动槽，不准备碎片或子完成位。
                var done=resources.Quests.All.Where(q=>q.Grade=="[epic]" && (legacy || q.Id!=3458)).Select(q=>q.Id).Append(3472).ToArray();
                created=repository.Create(owner,candidate with {Level=95,AcceptedStoryLevel=95,CompletedQuests=done,
                    ActiveMainQuests=legacy ? [new(0,3473,1)] : [new(0,3458,1),new(1,3473,1)],
                    InitialLearning=SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with {Experience=growth.GetLevelStartExperience(95)},resources.Learning)});
            }
            string assets=Assets(),saved="";
            using(var peer=Open(fixture,account))
            {
                var selected=peer.Select(prepared:true);
                if(!legacy)
                {
                    That(fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id IN(3458,3473)",created.Id) is [[2L]],
                        "父未完成的0/3不由重选擅自移除");
                    peer.Send(1430,[0,0,0,0]); peer.Expect(1,1430,[1]); peer.Barrier();
                    peer.Send(1457);
                    var notices=Drain(peer);
                    var active=notices.Single(p=>p.Opcode==574).Body;
                    That(!Contains(active,3473) && !Contains(active,3458),"真实1457后的574同步移除跳过父与有奖子槽");
                    That(notices.FindIndex(p=>p.Opcode==574)<notices.FindIndex(p=>p.Opcode==356),"清活动槽后再发完成表");
                }
                else
                    That(!SelectedContains(selected,3473),"旧档选角ACK4三十槽不发布已清理孤儿槽");
                That(fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3473",created.Id) is [[0L]] &&
                    fixture.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3473",created.Id) is [[0L]] && Assets()==assets,
                    "实际网络清理不伪造子完成位、不补片、不改背包金币与经验");
                saved=State();
            }
            using(var peer=Open(fixture,account))
            {
                var selected=peer.Select(prepared:true);
                That(!SelectedContains(selected,3473) && peer.CompletedQuestHistory[4+3473]==0 &&
                    !QuestLifecycleTcpTests.List(peer.AvailableQuests,95).Contains(3473) && State()==saved,
                    "断线重登无幽灵3473，清理回执不重复写入");
            }
            string Assets()=>fixture.Snapshot("SELECT gold,experience FROM characters WHERE id=$p0",created.Id)+
                fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",created.Id);
            string State()=>Assets()+fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id)+
                fixture.Snapshot("SELECT * FROM character_quest_skip_receipts WHERE character_id=$p0",created.Id);
        }
        static bool SelectedContains(WirePacket[] packets,ushort id)
        {
            var ack=packets.Single(p=>p.Kind==1 && p.Opcode==4).Body;
            That(ack.Length>=202,"ACK4包含完整三十槽，不以不存在的574自证");
            return Enumerable.Range(0,30).Any(slot=>U16(ack,22+6*slot)==id);
        }
        static bool Contains(byte[] body,ushort id)
        {
            That(body.Length==4+U32(body)*6,"574按实际u32数量完整解码");
            for(int i=0;i<U32(body);i++) if(U16(body,4+i*6)==id) return true;
            return false;
        }
    }
}
