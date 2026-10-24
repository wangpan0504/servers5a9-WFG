using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class QuestHellChallengeTcpTests
{
    static void VerifyHarlemContinuation(TcpTestFixture fixture,TcpTestResources resources)
    {
        var growth=SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
        var repository=new CharacterRepository(fixture.Database,resources.Quests);
        foreach(bool underfootHistory in new[] {false,true})
        {
            // 只准备6045之前的历史及当前场景，不预填目标接取、条件、完成位或奖励回执。
            var done=new HashSet<int>();
            AddPrerequisites(6045);
            if(underfootHistory) done.Add(3280);
            var area=resources.Towns.Areas.Values.First(a=>a.Level<=95 && a.Map(done).Npcs.Any(n=>n.Id==469));
            done.UnionWith(area.Quests);
            if(area.AnyQuests.Length>0) done.Add(area.AnyQuests[0]);
            That(!done.Contains(6045) && !done.Contains(6080),"场景准入前史不得预完成本次目标与后继");
            var bounds=area.Map(done).Bounds.First(b=>b.Conditions.Length==0 && b.Quests.All(done.Contains)).Bounds;
            var position=new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
            string account=underfootHistory ? "harlem-wire-normal" : "harlem-wire-legacy";
            Character created;
            using(var owner=new AccountOwners().Acquire(account,()=>{}))
            {
                var candidate=CharacterCreation.Prepare("Harlem"+underfootHistory,resources.Characters.Get(0),position,position,resources.Learning.Get);
                created=repository.Create(owner,candidate with {Level=95,AcceptedStoryLevel=95,CompletedQuests=done.Order().ToArray(),
                    InitialLearning=SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with {Experience=growth.GetLevelStartExperience(95)},resources.Learning)});
            }
            byte[] history=[],available=[];
            string saved="";
            using(var peer=Open(fixture,account))
            {
                peer.Select(prepared:true,resumeTown:true,room:false);
                That(QuestLifecycleTcpTests.List(peer.AvailableQuests,95).Contains(6045) && peer.CompletedQuestHistory[4+6045]==0,
                    "哈林尾链网络初始6045可接且未完成");
                Accept(peer,6045);
                peer.Send(33,Build(w=>{w.Write((ushort)33);w.Write((ushort)6045);w.Write((ushort)0);}));
                var condition=peer.Expect(1,33).Body;
                That(condition.Length==7 && condition[0]==1 && U16(condition,1)==6045 && U32(condition,3)==0,
                    "当前凯蒂场景通过实际33达成会面");
                peer.Barrier();
                SendFinish(peer,6045); var ack=peer.Expect(1,34).Body;
                That(ack[0]==1 && U16(ack,1)==6045,"实际34完成哈林尾链");
                var notices=Drain(peer);
                int list=notices.FindIndex(p=>p.Opcode==21),clear=notices.FindIndex(p=>p.Opcode==356);
                That(list>=0 && clear>list,"6045交付按候选21→历史356发布已提交状态");
                available=notices[list].Body; history=notices[clear].Body;
                var candidates=QuestLifecycleTcpTests.List(available,95);
                That(history[4+6045]==1 && !candidates.Contains(6045) && candidates.Contains(6080) && candidates.Contains(6217),
                    "尾链完成通知开放深渊及旅程选择而不重复弹出6045");
                saved=State();
                SendFinish(peer,6045); peer.Expect(1,34,[0,19,0]); peer.Barrier();
                That(State()==saved,"同连接重复尾链交付不变更历史或奖励");
            }
            using(var peer=Open(fixture,account))
            {
                peer.Select(prepared:true,resumeTown:true,room:false);
                Bytes(peer.CompletedQuestHistory,history,"哈林尾链完成历史重登一致");
                Bytes(peer.AvailableQuests,available,"哈林尾链候选列表重登一致");
                SendFinish(peer,6045); peer.Expect(1,34,[0,19,0]); peer.Barrier();
                That(State()==saved,"重登重复尾链领奖仍保持唯一回执");
                Accept(peer,6080);
                SendFinish(peer,6080); var ack=peer.Expect(1,34).Body;
                That(ack[0]==1 && U16(ack,1)==6080,"弹出的深渊资格任务可经真实网络接取并完成");
                var notices=Drain(peer);
                That(notices.Single(p=>p.Opcode==356).Body[4+6080]==1,
                    "后继完成位实际发布，不以Session可完成代替网络结果");
            }
            void AddPrerequisites(int id)
            {
                if(!resources.Quests.TryGet(id,out var quest)) return;
                foreach(int previous in quest.Definition?.PrerequisiteGroups.SelectMany(g=>g) ?? [])
                    if(previous>0 && done.Add(previous)) AddPrerequisites(previous);
            }
            string State()=>fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",created.Id)+
                fixture.Snapshot("SELECT * FROM character_completed_quests WHERE character_id=$p0 ORDER BY quest_id",created.Id);
        }
        void Accept(TcpPeer peer,ushort id)
        {
            peer.Send(31,Build(w=>{w.Write((ushort)31);w.Write(id);}));
            var ack=peer.Expect(1,31).Body;
            That(ack[0]==1 && U16(ack,1)==id,"哈林尾链及后继真实31接取 "+id);
            Drain(peer);
        }
        void SendFinish(TcpPeer peer,ushort id)=>peer.Send(34,Build(w=>{w.Write((ushort)34);w.Write(id);w.Write(ushort.MaxValue);w.Write((ushort)1);w.Write(ushort.MaxValue);}));
    }
}
