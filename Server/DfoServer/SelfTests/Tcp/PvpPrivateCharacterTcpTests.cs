using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class PvpPrivateCharacterTcpTests
{
    public static async Task RunAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        await using var f=new TcpTestFixture(source,Path.Combine(root,"pvp-private-character"),resources:resources);
        const string account="private-create-tcp";
        var repo=new CharacterRepository(f.Database,resources.Quests);
        using(var owner=new AccountOwners().Acquire(account,()=>{}))
            repo.Create(owner,new("Qualified",resources.Towns.Room,resources.Towns.InitialReturn){Level=85});
        await f.StartAsync();var endpoints=ChannelTcpTests.Endpoints(f);byte pvp=ChannelTcpTests.PvpChannelNumber(f);
        ushort created;
        using(var peer=Open(f,account))
        {
            var body=Create("PrivateTCP",0,1);
            peer.Send(5,body);peer.Expect(1,5,[0,19,0]);
            That(f.Query("SELECT count(*) FROM characters WHERE account=$p0",account) is [[1L]],"未通过872不能仅靠tail6伪造专用身份");
            peer.Send(872,Build(w=>{w.Write(0u);w.Write(1u);}));peer.Expect(1,872,[1,0]);
            peer.Send(5,body);var ack=peer.Expect(1,5).Body;created=U16(ack,1);peer.Expect(0,2);
            That(ack[0]==1 && f.Query("SELECT c.level,c.grow_type,p.recreate_days FROM characters c JOIN account_pvp_private_characters p ON p.character_id=c.id WHERE c.id=$p0",created)
                is [[85L,1L,30L]],"872后真实5创建85级原分支与30日删除冷却身份");
            peer.Send(5,Create("Replayed",0,1));peer.Expect(1,5,[0,19,0]);
            peer.Send(872,Build(w=>{w.Write(0u);w.Write(1u);}));peer.Expect(1,872,[0,19,0]);
            peer.Send(872,Build(w=>{w.Write(15u);w.Write(1u);}));peer.Expect(1,872,[0,19,0]);
            That(f.Query("SELECT count(*) FROM characters WHERE account=$p0",account) is [[2L]],"重复分支及错配职业资源不半建角色或发装备");
            foreach(byte grow in new byte[]{3,4})
            {
                peer.Send(872,Build(w=>{w.Write(12u);w.Write((uint)grow);}));peer.Expect(1,872,[1,0]);
                peer.Send(5,Create("Guardian"+grow,12,grow));var guardian=peer.Expect(1,5).Body;peer.Expect(0,2);
                That(guardian[0]==1 && f.Query("SELECT c.job,c.grow_type,p.recreate_days FROM characters c JOIN account_pvp_private_characters p ON p.character_id=c.id WHERE c.id=$p0",U16(guardian,1))
                    is [[12L,var actualGrow,30L]] && (long)actualGrow==grow,"守护者两缺配置分支通过872/5创建并持久化30天冷却");
                peer.Send(5,Create("ReplayG"+grow,12,grow));peer.Expect(1,5,[0,19,0]);
                peer.Send(872,Build(w=>{w.Write(12u);w.Write((uint)grow);}));peer.Expect(1,872,[0,19,0]);
            }
            That(f.Query("SELECT count(*) FROM characters WHERE account=$p0",account) is [[4L]],"两守护者分支各创建一次，重复请求不多发初装");
            peer.Send(4,[1,0]);peer.Expect(1,4,[0,96,0]);
            peer.Send(3,[0]);peer.Expect(1,3,[1]);peer.Closed();
        }
        using(var peer=Open(f,account,gamePort:endpoints[pvp],channelNumber:pvp))
        {
            peer.Select(1,room:false);peer.Barrier();
            That(f.Query("SELECT town FROM characters WHERE id=$p0",created) is [[10L]],"专用角色在决斗频道真实选角，落点在原决斗地区");
            string adventureBefore=AdventureState();
            long experienceBefore=(long)f.Query("SELECT experience FROM characters WHERE id=$p0",created)[0][0];
            peer.Send(31,[31,0,0x70,0x0A]);peer.Expect(1,31,[0,18,0]);
            peer.Send(31,[31,0,0x73,0x0A]);peer.Expect(1,31,[1,0x73,0x0A,1,0,0,0,0]);
            QuestLifecycleTcpTests.DrainNotifications(peer);peer.Barrier();
            That(f.Query("SELECT quest_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id BETWEEN 2670 AND 2675",created) is [[2675L]],
                "真实专用身份只接原风云儿入口，不越权普通TP链");
            var town=resources.Towns.Areas[(10,4)];
            var position=town.Gate ?? new DfoServer.GameWorld.Towns.Location(10,4,(ushort)town.Default.Bounds[0].Bounds.X,(ushort)town.Default.Bounds[0].Bounds.Y);
            peer.Travel(10,1,450,234,10,0);
            peer.Travel(10,4,position.X,position.Y,10,1);
            peer.Send(33,[33,0,0x73,0x0A,0,0]);peer.Expect(1,33,[1,0x73,0x0A,0,0,0,0]);
            peer.Send(34,[34,0,0x73,0x0A,255,255,1,0,255,255]);
            peer.Expect(0,105);var reward=peer.Expect(1,34).Body;
            That(reward[0]==1 && U16(reward,1)==2675,"专用首项在原NPC323会面后真实交付");
            QuestLifecycleTcpTests.DrainNotifications(peer);peer.Barrier();
            That(f.Query("SELECT quest_id FROM character_quest_completion_receipts WHERE character_id=$p0",created) is [[2675L]],
                "首项原奖励及完成身份只有一份事务回执");
            var arcade=new DfoServer.GameWorld.Pvp.PvpArcadeCatalog(resources.Archive);
            foreach(ushort quest in new ushort[]{2676,2677,2678,2679})
            {
                byte lo=(byte)quest,hi=(byte)(quest>>8);
                peer.Send(31,[31,0,lo,hi]);peer.Expect(0,1233,[0]);peer.Expect(0,1234);
                var accepted=peer.Expect(1,31).Body;
                That(accepted[0]==1 && U16(accepted,1)==quest,"专用后继按原前置逐条接取");
                QuestLifecycleTcpTests.DrainNotifications(peer);
                bool solo=quest is 2677 or 2679;
                if(!solo)
                {
                    peer.Send(29,[0,1,5,0,0,1,3]);That(peer.Expect(1,29).Body[0]==1,"专用自动加点实际提交训练快照");
                    peer.Send(50,[0,2,0,0,0,0xD1,0xB5,0,0,0,1,0]);peer.Expect(1,50,[1]);peer.Expect(0,41);
                    peer.Send(33,[33,0,lo,hi,0x20,0,0]);That(peer.Expect(1,33).Body[0]==1,"专用连招按原通道推进");
                    peer.Send(52,[0,254]);peer.Expect(0,43);
                }
                int mode=quest==2679 ? 2 : 80;
                var roundPlan=arcade.Resolve(0,1,mode) ?? throw new InvalidDataException("专用街机原资源缺失");
                peer.Send(50,[0,2,0,0,0,0xD1,0xB5,0,0,0,3,checked((byte)(mode+1))]);peer.Expect(1,50,[1]);peer.Expect(0,41);
                var used=new HashSet<int>();
                for(int round=1;round<=roundPlan.Mode.Rounds;round++)
                {
                    int opponent=roundPlan.Allowed[round-1].Order().First(id=>!roundPlan.RandomRounds.Contains(round) || !used.Contains(id));
                    if(roundPlan.RandomRounds.Contains(round)) used.Add(opponent);
                    Round(0,false);Round(1,true);
                    void Round(byte action,bool won)=>peer.Send(1287,Build(w=>
                    {w.Write(action);w.Write(opponent);w.Write(checked((byte)roundPlan.Mode.Rounds));w.Write(checked((byte)round));w.Write(0u);w.Write((byte)0);w.Write((byte)(won?1:0));w.Write((ushort)0);w.Write(0u);}));
                }
                if (quest is 2676 or 2679)
                {
                    peer.Expect(0,14);
                    PvpMissionTcpChecks.Progress(f, created, peer.Expect(0,578).Body);
                    int mission = quest == 2676 ? 604 : 605;
                    That(f.Query("SELECT count(*) FROM character_pvp_mission_rewards WHERE character_id=$p0 AND mission_id=$p1", created, mission) is [[1L]],
                        "专用角色完整道场胜利同样提交一次独立MSN袖珍罐，再继续原风云33流程");
                }
                byte channel=solo?(byte)0x10:(byte)0x40;
                peer.Send(33,[33,0,lo,hi,channel,0,0]);peer.Expect(1,33,[1,lo,hi,0,0,0,0]);
                peer.Send(52,[0,254]);peer.Expect(0,43);
                peer.Send(34,[34,0,lo,hi,255,255,1,0,255,255]);var finished=peer.Expect(1,34).Body;
                That(finished[0]==1 && U16(finished,1)==quest && finished[3]==60,"专用训练及难度2终末按真实60类型交付");
                QuestLifecycleTcpTests.DrainNotifications(peer);peer.Barrier();
            }
            That(f.Query("SELECT quest_id FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",created)
                is [[2675L],[2676L],[2677L],[2678L],[2679L]],"专用四步及原OR前置终末各有唯一完成账本");
            That(AdventureState()==adventureBefore && (long)f.Query("SELECT experience FROM characters WHERE id=$p0",created)[0][0]>experienceBefore,
                "专用五项原经验照常奖励角色，但不增加账号冒险团经验与胶囊额度");
            string completed=f.Snapshot();peer.Send(34,[34,0,0x77,0x0A,255,255,1,0,255,255]);peer.Expect(1,34,[0,19,0]);peer.Barrier();
            That(completed==f.Snapshot(),"专用终末重复领奖不改奖励与完成状态");

        }
        using(var peer=Open(f,account,gamePort:endpoints[47],channelNumber:47))
        {
            That(peer.LoginEndpoint[3]==8,"无双频道LOGIN保持本版类型8");
            peer.Select(1,room:false);peer.Barrier();
            That(peer.Id==created && f.Query("SELECT town FROM characters WHERE id=$p0",created) is [[10L]],
                "专用角色从传统转入无双仍沿用原身份及决斗地区");
        }
        await f.StopAsync();
        var privateCatalog=DfoServer.GameWorld.Characters.PvpPrivateCharacterCatalog.Load(resources.Archive);
        PvpPrivateCharacterTests.VerifyGuardianRecreation(resources.Towns,new(resources.Characters,privateCatalog,
            resources.Learning,resources.Growth,resources.ItemResources.Items,resources.Towns));
        string AdventureState()=>System.Text.Json.JsonSerializer.Serialize(f.Query(
            "SELECT level,experience,remainder,capsule_experience,brave_experience,opening_experience,excluded_experience FROM account_adventures WHERE account=$p0",account));
    }
    static byte[] Create(string name,byte job,byte grow)=>Build(w=>
    {var bytes=CharacterName.Encoding.GetBytes(name);w.Write(job);w.Write(bytes.Length);w.Write(bytes);w.Write(new byte[]{0,0,0,0,0,0,grow,0,grow,0,0,0});});
}
