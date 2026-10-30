using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class QuestHellChallengeTcpTests
{
    // #866 截图三项是外传第二步，不以普通深渊资格任务或静态支持数量代替实际接取。
    static void VerifyInfernoSidequests(TcpTestFixture fixture,TcpTestResources resources)
    {
        QuestHellChainTests.Run(resources);
        var growth=SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
        var repository=new CharacterRepository(fixture.Database,resources.Quests);
        foreach(ushort id in new ushort[] {5783,10104,10106})
        {
            var quest=resources.Quests.Get(id);
            int hell=InfernoQuestUnlock.Pairs.Single(pair=>pair.InfernoQuest==id).HellQuest;
            foreach(var scenario in new[] {(Level:(byte)95,Prerequisite:true),
                (Level:(byte)quest.MinimumLevel,Prerequisite:true), (Level:(byte)95,Prerequisite:false),
                (Level:(byte)(quest.MinimumLevel-1),Prerequisite:true)})
            {
                string suffix=scenario.Prerequisite ? "p" : "r";
                string account=$"inferno866-{id}-{scenario.Level}-{suffix}";
                Character created;
                using(var owner=new AccountOwners().Acquire(account,()=>{}))
                {
                    var candidate=CharacterCreation.Prepare($"Inf{id}L{scenario.Level}{suffix}",resources.Characters.Get(0),
                        resources.Towns.Room,resources.Towns.InitialReturn,resources.Learning.Get);
                    created=repository.Create(owner,candidate with {Level=scenario.Level,AcceptedStoryLevel=scenario.Level,
                        CompletedQuests=(scenario.Prerequisite ? quest.Prerequisites : []).Concat([3145,hell]).Distinct().ToArray(),
                        InitialLearning=SkillPointLedger.Synchronize(0,0,scenario.Level,candidate.InitialLearning with
                            {Experience=growth.GetLevelStartExperience(scenario.Level)},resources.Learning)});
                }
                bool eligible=scenario.Prerequisite && scenario.Level>=quest.MinimumLevel;
                string acceptance="",saved="";
                using(var peer=Open(fixture,account))
                {
                    peer.Select(prepared:true,room:true);
                    That(QuestLifecycleTcpTests.List(peer.AvailableQuests,scenario.Level).Contains(id)==eligible,
                        $"外传{id}按原等级和前置进入21候选：{scenario}");
                    if(eligible)
                        That(peer.CompletedQuestHistory[4+id]==0,
                            $"外传{id}前半步已完成时356不能用区域深渊资格冒充后半步已完成");
                    string before=State();
                    Accept(peer);
                    if(!eligible)
                    {
                        peer.Expect(1,31,[0,18,0]); peer.Barrier();
                        That(State()==before,$"外传{id}缺前置或低等级拒绝接取且零提交");
                        continue;
                    }
                    peer.Expect(1,31,Build(writer=>{writer.Write((byte)1);writer.Write(id);writer.Write(0u);writer.Write((byte)0);}));
                    peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
                    var active=fixture.Query("SELECT condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",created.Id,id);
                    That(active is [[0L,string]],$"外传{id}真实31建立已达成的sub1活动槽");
                    acceptance=(string)active[0][1];
                    That(Guid.TryParseExact(acceptance,"N",out var identity) && identity!=Guid.Empty &&
                        fixture.Query("SELECT action,acceptance_id FROM character_quest_lifecycle_receipts WHERE character_id=$p0 AND quest_id=$p1",created.Id,id)
                            is [["Accept",string receipt]] && receipt==acceptance && peer.CompletedQuestHistory[4+id]==0,
                        $"外传{id}接取与生命周期同事务保存，不提前标为完成");
                    saved=State(); Accept(peer); peer.Expect(1,31,[0,18,0]); peer.Barrier();
                    That(State()==saved,$"外传{id}重复31不替换原接取或发奖");
                    peer.Send(33,Build(writer=>{writer.Write((ushort)33);writer.Write(id);writer.Write((ushort)0);}));
                    peer.Expect(1,33,Build(writer=>{writer.Write((byte)1);writer.Write(id);writer.Write(0u);})); peer.Barrier();
                    That(State()==saved,$"外传{id}原生sub1归零33幂等");
                }
                using(var peer=Open(fixture,account))
                {
                    var selected=peer.Select(prepared:true,room:true);
                    byte[] selectedBody=selected.Single(packet=>packet.Kind==1 && packet.Opcode==4).Body;
                    That(Enumerable.Range(0,30).Any(slot=>U16(selectedBody,22+6*slot)==id && U32(selectedBody,24+6*slot)==0) &&
                        State()==saved && fixture.Query("SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",created.Id,id)
                            is [[string restored]] && restored==acceptance,
                        $"外传{id}真实断线后ACK4恢复原活动槽、进度和接取身份");
                    long gold=Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0",created.Id)[0][0]);
                    string inventory=fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",created.Id);
                    Finish(peer);
                    byte[] ack=peer.Expect(1,34).Body;
                    var notices=Drain(peer);
                    long goldAfter=Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0",created.Id)[0][0]);
                    That(ack.Length==29 && ack[0]==1 && U16(ack,1)==id && ack[3]==4 && ack[8]==0 && ack[9]==1 &&
                        U16(ack,10)==0 && U32(ack,12)==0 && goldAfter>gold && U32(ack,16)==goldAfter-gold && ack.Skip(20).All(value=>value==0),
                        $"外传{id}完整ACK34沿mode0发真实金币，不把裂缝副本号当物品或发mode3");
                    That(notices.Any(packet=>packet.Opcode==14 && U32(packet.Body,9)==goldAfter) &&
                        notices.Any(packet=>packet.Opcode==356 && packet.Body.Length==30004 && packet.Body[4+id]==1) &&
                        fixture.Query("SELECT acceptance_id,experience_amount,gold_amount FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=$p1",created.Id,id)
                            is [[string receipt,long experience,long granted]] && receipt==acceptance && experience>0 &&
                            (scenario.Level==95 ? U32(ack,4)==0 : experience==U32(ack,4)) && granted==goldAfter-gold &&
                        fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",created.Id,id) is [[0L]] &&
                        fixture.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",created.Id,id) is [[1L]] &&
                        inventory==fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",created.Id),
                        $"外传{id}沿原接取提交一次奖励、完成位和清槽，已有实体不变");
                    saved=State(); Finish(peer); peer.Expect(1,34,[0,19,0]); peer.Barrier();
                    That(State()==saved,$"外传{id}重复34不重复领奖");
                }
                using(var peer=Open(fixture,account))
                {
                    peer.Select(prepared:true,room:true);
                    That(peer.CompletedQuestHistory[4+id]==1 && !QuestLifecycleTcpTests.List(peer.AvailableQuests,scenario.Level).Contains(id) && State()==saved,
                        $"外传{id}完成后重连恢复真实完成位且不重新开放");
                    Accept(peer); peer.Expect(1,31,[0,18,0]); peer.Barrier();
                    That(State()==saved,$"外传{id}完成后再次接取拒绝且原奖励回执不变");
                }
                string State()=>fixture.Snapshot("SELECT gold,experience,revision FROM characters WHERE id=$p0",created.Id)+
                    fixture.Snapshot("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id)+
                    fixture.Snapshot("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 ORDER BY quest_id",created.Id)+
                    fixture.Snapshot("SELECT quest_id,action,acceptance_id FROM character_quest_lifecycle_receipts WHERE character_id=$p0 ORDER BY operation_id",created.Id)+
                    fixture.Snapshot("SELECT quest_id,acceptance_id,experience_amount,gold_amount FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",created.Id);
                void Accept(TcpPeer peer)=>peer.Send(31,Build(writer=>{writer.Write((ushort)31);writer.Write(id);}));
                void Finish(TcpPeer peer)=>peer.Send(34,Build(writer=>{writer.Write((ushort)34);writer.Write(id);
                    writer.Write(ushort.MaxValue);writer.Write((ushort)1);writer.Write(ushort.MaxValue);}));
            }
        }
    }
}
