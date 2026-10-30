using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class PvpTrainingTcpTests
{
    public static async Task RunAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        await DailyAsync(source, root, resources);
        await PracticeAsync(source, root, resources);
        await using var f=new TcpTestFixture(source,Path.Combine(root,"pvp-training"),resources:resources);
        const string account="training-tcp";
        var repo=new CharacterRepository(f.Database,resources.Quests);
        var town=resources.Towns.Areas[(10,4)];
        var position=town.Gate ?? new Location(10,4,(ushort)town.Default.Bounds[0].Bounds.X,(ushort)town.Default.Bounds[0].Bounds.Y);
        var growth=SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
        ushort id;
        using(var owner=new AccountOwners().Acquire(account,()=>{}))
        {
            var seed=CharacterCreation.Prepare("TrainingTCP",resources.Characters.Get(0),position,resources.Towns.InitialReturn,resources.Learning.Get);
            // 五环均通过真实接取、场景条件和交付，不预置任何风云完成位。
            id=repo.Create(owner,seed with {Level=60,AcceptedStoryLevel=60,
                InitialLearning=SkillPointLedger.Synchronize(0,0,60,seed.InitialLearning with
                    {Experience=growth.GetLevelStartExperience(60)},resources.Learning)}).Id;
        }
        byte[] request=[0,2,0,0,0,0xD1,0xB5,0,0,0,3,81];
        await f.StartAsync();
        var endpoints=ChannelTcpTests.Endpoints(f);
        byte pvp=ChannelTcpTests.PvpChannelNumber(f);
        using(var peer=Open(f,account,gamePort:endpoints[pvp],channelNumber:pvp))
        {
            peer.Send(50,request);peer.Expect(1,50,[0,19,0]);
            peer.Send(29,[0,1,5,0,0,1,3]);peer.Expect(1,29,[0,0xD9,0]);
            peer.Select(prepared:true);peer.Barrier();
            var adventureBefore=AdventureProgress();
            long experienceBefore=(long)f.Query("SELECT experience FROM characters WHERE id=$p0",id)[0][0];
            Meet(peer,2670);
            peer.Send(31,[31,0,0x6F,0x0A]);peer.Expect(1,31,[1,0x6F,0x0A,1,0,0,0,0]);
            QuestLifecycleTcpTests.DrainNotifications(peer);
            peer.Send(33,[33,0,0x6F,0x0A,0,0]);peer.Expect(1,33,[0,19,0]);peer.Barrier();
            peer.Travel(10,1,450,234,10,0);
            peer.Travel(10,4,position.X,position.Y,10,1);
            Meet(peer,2671,accepted:true);
            peer.Send(31,[31,0,0x70,0x0A]);
            peer.Expect(0,1233,[0]);
            That(peer.Expect(0,1234).Body.Length>=10,"已在道场首次接取时，ACK31前也装载独立技能双树");
            peer.Expect(1,31,[1,0x70,0x0A,1,2,4,0,0]);
            QuestLifecycleTcpTests.DrainNotifications(peer);
            peer.Send(34,[34,0,0x70,0x0A,255,255,1,0,255,255]);peer.Expect(1,34,[0,19,0]);peer.Barrier();
            string ordinarySkills=OrdinarySkills();
            peer.Send(491,[0,0]);peer.Expect(1,491,[1,0,1]);peer.Expect(0,1234);
            peer.Send(29,[1,1,5,0,0,1,3]);peer.Expect(1,29,[0,0xD9,0]);
            peer.Send(29,[0,1,5,0,0,1,3]);
            byte[] learned=peer.Expect(1,29).Body;
            That(learned[0]==1 && learned[1]==0 && learned[^1]==3,"训练自动加点返回实际mode3，不按普通技能模式回复");
            peer.Send(28,[0,learned[7],11,255,255,255,255,0]);peer.Expect(1,28,[1,0,learned[7],11]);
            peer.Barrier();
            That(ordinarySkills==OrdinarySkills(),"训练重置、自动学习和换槽不修改角色普通双树点数或绑定");
            That(f.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=2672",id) is [[262656L]],
                "自动加点与独立快照同事务仅清第一通道");
            string unchangedTraining=f.Snapshot();
            peer.Send(29,[0,0,3]);
            byte[] unchangedAck=peer.Expect(1,29).Body;
            That(unchangedAck.Length==8 && unchangedAck[0]==1 && unchangedAck[6]==0 && unchangedAck[7]==3,
                "重复自动加点零变化仍确认mode3，结束客户端等待");
            peer.Barrier();
            That(unchangedTraining==f.Snapshot(),"已确认训练零变化不重复写入或推进其它任务通道");
            That(f.Query("SELECT COUNT(*) FROM character_pvp_training_skills WHERE character_id=$p0",id) is [[1L]],"训练技能独立持久化");
            peer.Send(33,[33,0,0x70,0x0A,0x10,0,0]);peer.Expect(1,33,[1,0x70,0x0A,0,2,4,0]);
            string before=f.Snapshot();
            string questsBefore = f.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0", id);
            peer.Send(50,request);peer.Expect(1,50,[1]);CheckRoom(peer.Expect(0,41).Body,false);
            peer.Send(50,request);peer.Expect(1,50,[0,19,0]);
            peer.Send(33,[33,0,0x70,0x0A,0x40,0,0]);peer.Expect(1,33,[0,19,0]);
            // 缺少开始、篡改对手和重复开始均不产生正式完成事实。
            Round(peer,1,1201,1,true);Round(peer,0,9999,1,false);
            Round(peer,0,1201,1,false);Round(peer,0,1201,1,false);Round(peer,1,1201,1,true);
            Round(peer,0,1201,2,false);Round(peer,0,1202,2,false);Round(peer,1,1202,2,true);
            peer.Expect(0,14);
            PvpMissionTcpChecks.Update(peer, f);
            Round(peer,1,1202,2,true);peer.Barrier();
            That(questsBefore == f.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0", id) &&
                f.Query("SELECT mission_id FROM character_pvp_mission_rewards WHERE character_id=$p0", id) is [[604L]],
                "道场MSN奖励独立原子提交，风云任务条件仍须原33通道消费");
            peer.Send(33,[33,0,0x70,0x0A,0x10,1,0]);peer.Expect(1,33,[0,19,0]);
            peer.Send(33,[33,0,0x70,0x0A,0x40,1,0]);peer.Expect(1,33,[0,19,0]);
            peer.Send(33,[33,0,0x70,0x0A,0x40,0,0]);peer.Expect(1,33,[1,0x70,0x0A,0,2,0,0]);peer.Barrier();
            That(f.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=2672",id) is [[512L]],
                "全轮胜利只清原任务第2通道，保留连招计数，不恢复已提交加点通道，不直接归零发奖");
            before=f.Snapshot();
            peer.Send(33,[33,0,0x70,0x0A,0x40,0,0]);peer.Expect(1,33,[1,0x70,0x0A,0,2,0,0]);peer.Barrier();
            That(before==f.Snapshot(),"重复消费同一正式事实不新增事务或revision");
            peer.Send(12,Convert.FromHexString("000304000000000500000000000007"));peer.Expect(1,12,[0,21,0]);
            peer.Send(52,[0,254]);peer.Expect(0,43,[(byte)id,(byte)(id>>8),10,1,0,0,255,255]);PvpArenaTcpTests.TownReturn(peer, position);peer.Barrier();
            That(before==f.Snapshot(),"训练退出清房间且不改原位置、角色资产及接取UUID");
            Round(peer,1,1202,2,true);peer.Barrier();
            peer.Send(50,request);peer.Expect(1,50,[1]);CheckRoom(peer.Expect(0,41).Body,false);
            // 共享离队入口仍支持主线训练房间退出，不能误走城镇队伍或重复注册CMD13。
            peer.Send(13);peer.Expect(0,43,[(byte)id,(byte)(id>>8),10,1,0,0,255,255]);PvpArenaTcpTests.TownReturn(peer, position);peer.Barrier();
            peer.Send(50,request);peer.Expect(1,50,[1]);CheckRoom(peer.Expect(0,41).Body,false);
            peer.Send(7);peer.Expect(1,7,[1]);peer.Expect(0,2);
            peer.Select(prepared:true);peer.Barrier();
            peer.Send(13);peer.Expect(1,13,[0,19,0]);
            Round(peer,1,1202,2,true);peer.Barrier();
            That(f.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=2672",id) is [[512L]],
                "换角重连不恢复旧街机事实，更不接受迟到结算改任务");
            peer.Travel(10,1,450,234,10,0);
            peer.Travel(10,4,position.X,position.Y,10,1,beforeLocation:p=>
            {
                p.Expect(0,1233,[0]);var snapshot=p.Expect(0,1234).Body;
                int count=snapshot[0];
                That(Enumerable.Range(0,count).Any(i=>snapshot[1+i*4]==11 && U16(snapshot,2+i*4)==5 && snapshot[4+i*4]==learned[10]),
                    "重新入场恢复训练独立槽位及等级，不从普通树覆写");
            });
            That(ordinarySkills==OrdinarySkills(),"小退重登后普通技能双树仍不受训练加点污染");
            byte[] combo=[0,2,0,0,0,0xD1,0xB5,0,0,0,1,0];
            peer.Send(33,[33,0,0x70,0x0A,0x20,0,0]);peer.Expect(1,33,[0,19,0]);
            peer.Send(50,combo);peer.Expect(1,50,[1]);CheckRoom(peer.Expect(0,41).Body,false,6);
            peer.Send(50,combo);peer.Expect(1,50,[0,19,0]);
            before=f.Snapshot();
            Round(peer,0,1201,1,false);Round(peer,1,1201,1,true);
            peer.Send(33,[33,0,0x70,0x0A,0x40,0,0]);peer.Expect(1,33,[0,19,0]);
            peer.Send(33,[33,0,0x70,0x0A,0x20,1,0]);peer.Expect(1,33,[0,19,0]);peer.Barrier();
            That(before==f.Snapshot(),"连招房间不接街机报告、错误通道或任意客户端计数");
            peer.Send(33,[33,0,0x70,0x0A,0x20,0,0]);peer.Expect(1,33,[1,0x70,0x0A,0,0,0,0]);peer.Barrier();
            That(f.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=2672",id) is [[0L]],
                "独立连招房间只清原生中间通道；与之前技能和街机事务合成完整条件");
            before=f.Snapshot();
            peer.Send(33,[33,0,0x70,0x0A,0x20,0,0]);peer.Expect(1,33,[1,0x70,0x0A,0,0,0,0]);peer.Barrier();
            That(before==f.Snapshot(),"连招成功重放不额外写库或发奖");
            peer.Send(52,[1,254]);peer.Send(52,[0,0]);peer.Barrier();
            That(before==f.Snapshot(),"未证席位或状态不改当前训练与资产");
            peer.Send(52,[0,254]);peer.Expect(0,43,[(byte)id,(byte)(id>>8),6,1,0,0,255,255]);PvpArenaTcpTests.TownReturn(peer, position);
            peer.Send(52,[0,254]);peer.Barrier();
            peer.Send(33,[33,0,0x70,0x0A,0x20,0,0]);peer.Expect(1,33,[0,19,0]);
            peer.Send(50,combo);peer.Expect(1,50,[0,19,0]);
            peer.Barrier();That(before==f.Snapshot(),"退出后迟到报告和已完成条件不能重新建训练实例");
            peer.Send(34,[34,0,0x70,0x0A,255,255,1,0,255,255]);
            byte[] reward=peer.Expect(1,34).Body;
            That(reward[0]==1 && U16(reward,1)==2672 && reward[3]==60,"实际训练交付保留类型60而非伪造会面分类");
            QuestLifecycleTcpTests.DrainNotifications(peer);
            That(f.Query("SELECT quest_id FROM character_completed_quests WHERE quest_id=2672") is [[2672L]] &&
                f.Query("SELECT condition_type FROM character_quest_completion_receipts WHERE quest_id=2672") is [[60L]],
                "2672真实接取、三通道推进与原奖励同事务闭环");
            peer.Send(31,[31,0,0x71,0x0A]);peer.Expect(0,1233,[0]);peer.Expect(0,1234);
            peer.Expect(1,31,[1,0x71,0x0A,1,0,0,0,0]);
            QuestLifecycleTcpTests.DrainNotifications(peer);
            peer.Send(33,[33,0,0x71,0x0A,0x40,0,0]);peer.Expect(1,33,[0,19,0]);peer.Barrier();
            That(f.Query("SELECT condition_state FROM character_active_main_quests WHERE quest_id=2673") is [[1L]],
                "后继2673可真实接取，但不能复用上个任务或退出实例的胜利事实");
            peer.Send(50,request);peer.Expect(1,50,[1]);CheckRoom(peer.Expect(0,41).Body,false);
            Round(peer,0,1201,1,false);Round(peer,1,1201,1,true);
            Round(peer,0,1202,2,false);Round(peer,1,1202,2,true);
            peer.Send(33,[33,0,0x71,0x0A,0x40,0,0]);peer.Expect(1,33,[0,19,0]);
            peer.Send(33,[33,0,0x71,0x0A,0x10,0,0]);peer.Expect(1,33,[1,0x71,0x0A,0,0,0,0]);
            peer.Send(52,[0,254]);peer.Expect(0,43,[(byte)id,(byte)(id>>8),10,1,0,0,255,255]);PvpArenaTcpTests.TownReturn(peer, position);
            peer.Send(34,[34,0,0x71,0x0A,255,255,1,0,255,255]);
            reward=peer.Expect(1,34).Body;
            That(reward[0]==1 && U16(reward,1)==2673 && reward[3]==60,"2673按mode2只消费胜利通道并交付原奖励");
            QuestLifecycleTcpTests.DrainNotifications(peer);
            That(f.Query("SELECT quest_id FROM character_quest_completion_receipts WHERE quest_id IN(2672,2673) ORDER BY quest_id")
                is [[2672L],[2673L]],"真实连续两项各自只有一份领奖回执");
            // 2674原客户端先1269取得秘籍，再33确认第三通道；领奖消费一个秘籍，不能额外留书。
            peer.Send(31,[31,0,0x72,0x0A]);peer.Expect(0,1233,[0]);peer.Expect(0,1234);
            peer.Expect(1,31,[1,0x72,0x0A,1,2,4,0,0]);QuestLifecycleTcpTests.DrainNotifications(peer);
            before=f.Snapshot();peer.Send(1269,[]);peer.Barrier();
            That(before==f.Snapshot(),"无胜利实例的空1269不能给秘籍");
            peer.Send(29,[0,0,3]);
            That(peer.Expect(1,29).Body is [1,0,_,_,_,_,0,3],"已有方案无需升级也可真实提交末项训练加点事实");
            peer.Send(50,combo);peer.Expect(1,50,[1]);CheckRoom(peer.Expect(0,41).Body,false,6);
            peer.Send(33,[33,0,0x72,0x0A,0x20,0,0]);peer.Expect(1,33,[1,0x72,0x0A,0,0,4,0]);
            peer.Send(52,[0,254]);peer.Expect(0,43,[(byte)id,(byte)(id>>8),6,1,0,0,255,255]);PvpArenaTcpTests.TownReturn(peer, position);
            peer.Send(50,request);peer.Expect(1,50,[1]);CheckRoom(peer.Expect(0,41).Body,false);
            before=f.Snapshot();peer.Send(1269,[]);peer.Barrier();That(before==f.Snapshot(),"末轮未胜利不能预领秘籍");
            Round(peer,0,1201,1,false);Round(peer,1,1201,1,true);
            Round(peer,0,1202,2,false);Round(peer,1,1202,2,true);
            peer.Send(33,[33,0,0x72,0x0A,0x40,0,0]);peer.Expect(1,33,[0,19,0]);
            That(ManualCount()==0,"末项不能用33跳过秘籍发放链");
            peer.Send(1269,[]);peer.Expect(0,14);peer.Barrier();
            That(ManualCount()==1 && f.Query("SELECT condition_state FROM character_active_main_quests WHERE quest_id=2674") is [[0L]],
                "实际秘籍和第三通道同事务持久化");
            before=f.Snapshot();peer.Send(1269,[]);peer.Barrier();That(before==f.Snapshot(),"同一胜利不能重复领秘籍");
            peer.Send(33,[33,0,0x72,0x0A,0x40,0,0]);peer.Expect(1,33,[1,0x72,0x0A,0,0,0,0]);
            peer.Send(52,[0,254]);peer.Expect(0,43,[(byte)id,(byte)(id>>8),10,1,0,0,255,255]);PvpArenaTcpTests.TownReturn(peer, position);
            long tp=(long)f.Query("SELECT bonus_tp FROM character_tp_sources WHERE character_id=$p0",id)[0][0];
            peer.Send(34,[34,0,0x72,0x0A,255,255,1,0,255,255]);reward=peer.Expect(1,34).Body;
            That(reward[0]==1 && U16(reward,1)==2674 && reward[3]==60,"末项仍按真实训练条件和TP合同交付");
            QuestLifecycleTcpTests.DrainNotifications(peer);peer.Barrier();
            That(ManualCount()==0 && f.Query("SELECT bonus_tp FROM character_tp_sources WHERE character_id=$p0",id) is [[long savedTp]] && savedTp==tp+2,
                "2674只增加原2TP且消费实际秘籍，不额外发放秘籍奖励");
            var adventureAfter=AdventureProgress();
            That((long)f.Query("SELECT experience FROM characters WHERE id=$p0",id)[0][0]>experienceBefore &&
                (adventureAfter.Level>adventureBefore.Level || adventureAfter.Level==adventureBefore.Level && adventureAfter.Experience>adventureBefore.Experience),
                "普通训练原经验仍正常增加角色与账号冒险团，不受专用角色隔离误伤");
            ordinarySkills=OrdinarySkills();
            peer.Send(1268,[1]);var copied=SkillPacketsTests.DecodeInitialized(peer.Expect(0,19).Body);peer.Barrier();
            That(copied[0].Skills.Any(skill=>skill.Id==5 && skill.Level>1) && ordinarySkills==OrdinarySkills() &&
                f.Query("SELECT json_extract(state_json,'$.CopyChoice') FROM character_pvp_training_skills WHERE character_id=$p0",id) is [[1L]],
                "1268选择1只复制训练活动页到独立正式PK技能，不污染普通双树");
            before=f.Snapshot();peer.Send(1268,[1]);peer.Send(1268,[2]);peer.Send(1269,[]);peer.Barrier();
            That(before==f.Snapshot(),"复制决定持久化后重放或取消不再覆盖技能，也不重新给秘籍");
            peer.Travel(10,1,550,330,10,4);
            peer.Send(29,[0,1,5,0,0,1,0]);var arenaLearned=SkillPacketsTests.DecodeSuccess(peer.Expect(1,29).Body);
            peer.Barrier();
            That(arenaLearned.Skills.Single().Level==copied[0].Skills.Single(skill=>skill.Id==5).Level+1 &&
                ordinarySkills==OrdinarySkills(),"离开道场后普通CMD29继续写正式PK域，普通方案保持完整");
            before=f.Snapshot();
            for(int repeat=0;repeat<2;repeat++)
            {
                peer.Send(29,[0,0,1]);
                That(peer.Expect(1,29).Body is [1,0,_,_,_,_,0,1],"正式决斗零变化自动加点正常确认并保留模式");
                peer.Barrier();
            }
            peer.Send(29,[0,0,0]);peer.Expect(1,29,[0,0xD9,0]);peer.Barrier();
            That(before==f.Snapshot(),"决斗重复自动加点及非法空手工请求均不改存档，拒绝后仍可继续操作");
            peer.Send(331,[1,5,0,3,3,2,4]);peer.Expect(1,331,[1]);peer.Barrier();
            var commands=System.Text.Json.JsonSerializer.Deserialize<PvpTrainingSkills>((string)f.Query(
                "SELECT state_json FROM character_pvp_training_skills WHERE character_id=$p0",id)[0][0])!;
            That(commands.Arena!.Trees[0].Skills.Single(skill=>skill.Id==5).Command=="030204" && ordinarySkills==OrdinarySkills(),
                "决斗指令保存原始键序且不污染PVE或训练技能，也不回放19清掉编辑图标");
            peer.Send(3,[0]);peer.Expect(1,3,[1]);peer.Closed();
        }
        using(var peer=Open(f,account,gamePort:endpoints[pvp],channelNumber:pvp))
        {
            peer.Select(prepared:true);
            That(SkillPacketsTests.DecodeInitialized(peer.SkillInfo)[0].Skills.Single(skill=>skill.Id==5).Level>1,
                "决斗频道重登恢复独立正式PK技能");
            foreach(ushort questId in new ushort[]{2670,2671,2672,2673,2674})
            {
                That(peer.CompletedQuestHistory[questId+4]==1,"重登保留训练完成位");
                peer.Send(34,[34,0,(byte)(questId&255),(byte)(questId>>8),255,255,1,0,255,255]);
                peer.Expect(1,34,[0,19,0]);peer.Barrier();
            }
            That(f.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id IN(2672,2673,2674)") is [[3L]],
                "重登重发不重复领取三项训练奖励");
        }
        using(var peer=Open(f,account))
        {
            peer.Select(prepared:true);
            That(SkillPacketsTests.DecodeInitialized(peer.SkillInfo)[0].Skills.Single(skill=>skill.Id==5).Level==1,
                "普通频道重登仍回放普通方案，不把决斗复制反写刷图技能");
        }
        const string cancelAccount="training-cancel";
        ushort cancelId;
        using(var owner=new AccountOwners().Acquire(cancelAccount,()=>{}))
        {
            var seed=CharacterCreation.Prepare("CancelTCP",resources.Characters.Get(0),position,resources.Towns.InitialReturn,resources.Learning.Get);
            var created=repo.Create(owner,seed with {Level=60,AcceptedStoryLevel=60,CompletedQuests=[2674],
                InitialLearning=SkillPointLedger.Synchronize(0,0,60,seed.InitialLearning with
                    {Experience=growth.GetLevelStartExperience(60)},resources.Learning)});
            cancelId=created.Id;
            var profile=new PvpTrainingSkills(created.Learning,2674,Guid.NewGuid()) {Arena=created.Learning};
            f.Sql("INSERT INTO character_pvp_training_skills VALUES($p0,$p1,$p2,$p3)",created.Id,
                System.Text.Json.JsonSerializer.Serialize(profile),Guid.NewGuid().ToString("N"),created.Revision);
        }
        using(var peer=Open(f,cancelAccount,gamePort:endpoints[pvp],channelNumber:pvp))
        {
            peer.Select(prepared:true);peer.Barrier();
            var before=SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            peer.Send(1268,[2]);var cancelled=SkillPacketsTests.DecodeInitialized(peer.Expect(0,19).Body);peer.Barrier();
            That(System.Text.Json.JsonSerializer.Serialize(before)==System.Text.Json.JsonSerializer.Serialize(cancelled) &&
                f.Query("SELECT json_extract(state_json,'$.CopyChoice') FROM character_pvp_training_skills WHERE character_id=$p0",cancelId) is [[2L]],
                "1268取消保存真实决定，完整回放仍保留原正式PK双方案");
            peer.Send(3,[0]);peer.Expect(1,3,[1]);peer.Closed();
        }
        using(var peer=Open(f,cancelAccount,gamePort:endpoints[pvp],channelNumber:pvp))
        {
            peer.Select(prepared:true);peer.Barrier();string before=f.Snapshot();
            peer.Send(1268,[1]);peer.Barrier();
            That(before==f.Snapshot() && f.Query("SELECT json_extract(state_json,'$.CopyChoice') FROM character_pvp_training_skills WHERE character_id=$p0",cancelId) is [[2L]],
                "取消后重登及迟到确认不覆盖决定或角色版本");
        }
        await f.StopAsync();
        static void Meet(TcpPeer peer,ushort quest,bool accepted=false)
        {
            byte lo=(byte)quest,hi=(byte)(quest>>8);
            if(!accepted)
            {
                peer.Send(31,[31,0,lo,hi]);peer.Expect(1,31,[1,lo,hi,1,0,0,0,0]);
                QuestLifecycleTcpTests.DrainNotifications(peer);
            }
            peer.Send(33,[33,0,lo,hi,0,0]);peer.Expect(1,33,[1,lo,hi,0,0,0,0]);peer.Barrier();
            peer.Send(34,[34,0,lo,hi,255,255,1,0,255,255]);
            var reward=peer.Expect(1,34).Body;
            That(reward[0]==1 && U16(reward,1)==quest && reward[3]==4,"真实NPC会面按本版type4交付并开放下一环");
            QuestLifecycleTcpTests.DrainNotifications(peer);
        }
        long ManualCount()=>(long)f.Query("SELECT COALESCE(SUM(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=10158452",id)[0][0];
        (long Level,long Experience) AdventureProgress()
        {var row=f.Query("SELECT level,experience FROM account_adventures WHERE account=$p0",account)[0];return ((long)row[0],(long)row[1]);}
        string OrdinarySkills()=>System.Text.Json.JsonSerializer.Serialize(new[]{
            f.Query("SELECT context,sp,tp FROM character_skill_points WHERE character_id=$p0 ORDER BY context",id),
            f.Query("SELECT context,slot,skill_id,level FROM character_skill_bindings WHERE character_id=$p0 ORDER BY context,slot",id)});
        void CheckRoom(byte[] body,bool removed,byte mode=10)
        {
            That(body.Length==43 && U16(body,0)==1 && U16(body,2)==id && body[4]==0 && U32(body,5)==0 && body[9]==0 &&
                body[10]==(removed?255:0) && U16(body,11)==0 && body[13]==mode && body[38]==0 && U32(body,39)==0,
                "NOTI41包含默认标题长度、固定八席、队长索引与地图序号");
            for(int seat=0;seat<8;seat++) That(body[14+seat*3]==0 && U16(body,15+seat*3)==(!removed && seat==0?id:ushort.MaxValue),
                "仅当前角色占席；空席严格用客户端FFFF哨兵");
        }
        static void Round(TcpPeer peer,byte action,int opponent,byte round,bool won)=>peer.Send(1287,Build(w=>
        {w.Write(action);w.Write(opponent);w.Write((byte)2);w.Write(round);w.Write(0u);w.Write((byte)0);w.Write((byte)(won?1:0));w.Write((ushort)0);w.Write(0u);}));
    }
}
