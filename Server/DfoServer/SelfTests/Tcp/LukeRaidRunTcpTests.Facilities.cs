using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class LukeRaidRunTcpTests
{
    // 正常Runtime、四人Party及原662帧；不Attach Battle、不改正式Run或设施状态。
    internal static void Facilities(TcpTestFixture fixture,TcpPeer[] peers,byte[] roster,RaidDungeonCatalog catalog,TcpTestResources resources)
    {
        var members=peers.Take(4).ToArray();var others=peers.Skip(4).ToArray();
        var captain=members.Single(peer=>peer.Id==Leader(roster));
        var reporter=members.First(peer=>peer!=captain);
        var instance=(string)fixture.Query("SELECT instance_id FROM character_raid_runs WHERE character_id=$p0 AND state=0",captain.Id)[0][0]!;
        foreach(var (dungeon,objectId,symbol) in new[]{(5054,64249,117),(5054,64249,117),(5055,64251,118),(5056,64252,119)})
        {
            var entered=Enter(members,captain,dungeon);
            foreach(var peer in others)Drain(peer,true);
            byte[] info=entered[0].Single(packet=>packet.Opcode==28).Body;
            byte[] map=entered[0].Single(packet=>packet.Opcode==29).Body;
            var visited=new HashSet<DungeonPoint>{new(map[0],map[1])};
            var maze=catalog.Find(dungeon)!.Mazes.Single(row=>row.SourceOrdinal==info[7]);
            var facilities=maze.MapSpecification!.Where(room=>SelfTestCatalogs.Rooms(resources.Archive)
                .Get(room,requirePassiveDefinitions:false,allowUnresolvedSpecialMetadata:true).PassiveObjects.Any(obj=>obj.ObjectId==objectId)).ToArray();
            That(facilities.Length==2,"当前实际maze恰有两个目标设施格，不硬编码原生对象ID或房间序号");
            Rejected(reporter,Body(symbol),"原16后加载屏障不接受662");
            LoadFacilityRoom();
            var first=facilities.OrderBy(room=>QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),room.Position).Length).First();
            var ordered=new[]{first,facilities.Single(room=>room!=first)};
            for(int index=0;index<ordered.Length;index++)
            {
                MoveTo(ordered[index].Position);
                That(U32(map,14)==ordered[index].Id,"设施预期来自当前TCP map与实际资源格");
                if(dungeon!=5056&&Living().Length>0)Rejected(reporter,Body(symbol),"birth/extinction活怪未清不接受设施完成");
                if(dungeon!=5056)KillRoom(); // birth/extinction的资源门；spread刻意在有活怪时报告。
                else That(Living().Length>0,"spread网络样本保留活怪，不能靠清房冒充设施完成");
                if(index==0)
                {
                    Rejected(others[0],Body(symbol),"另一分队Town成员不能借当前Run设施");
                    foreach(var body in new[]{Body(116),Body(symbol,2),Body(symbol,1,0),Body(symbol)[..8]})
                        Rejected(reporter,body,"白名单/值/操作/长度失败不产生662成功或symbol更新");
                    Rejected(reporter,Body(symbol==117?118:117),"合法symbol不能越当前房设施绑定");
                    // 已提交的玩家死亡不同于pendingDeath事务；不凭测试给原移动规则新增全队存活条件。
                    var dead=members.Last(peer=>peer!=reporter&&peer!=captain);
                    dead.Send(40,[0x74,0,4,1,0,0]);
                    That(Collect(dead).Count(packet=>packet is {Kind:0,Opcode:32}&&U16(packet.Body)==dead.Id&&packet.Body[2]==0)==members.Length,
                        "真实40已向全队提交玩家死亡，不把未提交请求当死亡事实");
                    reporter.Send(662,Body(symbol));CheckCount(Collect(reporter),symbol,1);
                    dead.Send(41,[(byte)dead.Id,(byte)(dead.Id>>8)]);
                    That(Collect(dead).Count(packet=>packet is {Kind:1,Opcode:41,Body:[1,_,_,0]})==1,"真实41恢复玩家生命，已完成设施不因此重置");
                }
                if(dungeon==5056&&index==1)
                {
                    string before=fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY character_id,instance_id");
                    fixture.Sql($"CREATE TRIGGER luke_facility_failure BEFORE UPDATE OF phase0_receipt ON character_raid_runs WHEN OLD.character_id={peers[^1].Id} BEGIN SELECT RAISE(ABORT,'luke facility last member rollback'); END");
                    reporter.Send(662,Body(symbol));
                    bool closed=false;
                    for(int budget=0;budget<256&&!closed;budget++)
                    {
                        try { var packet=reporter.Read();That(packet.Opcode is not (662 or 601),"资格SQL失败前不得确认662成功或阶段结果"); }
                        catch(EndOfStreamException){closed=true;}
                    }
                    That(closed,"末成员SQL异常沿真实连接关闭，不假造失败码");
                    That(fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY character_id,instance_id")==before,
                        "设施触发的末成员SQL失败回滚全团资格，不留下部分receipt");
                    reporter.Dispose();members=members.Where(peer=>peer!=reporter).ToArray();
                    foreach(var peer in members.Concat(others))
                        That(!Drain(peer,true).Any(packet=>packet.Opcode==601||packet is {Kind:0,Opcode:587,Body:[3,0]}),"SQL未成功保持旧阶段且没有结果");
                    fixture.Sql("DROP TRIGGER luke_facility_failure");
                    captain.Send(662,Body(symbol));
                    var retry=Collect(captain);
                    NoReward(retry,"SQL恢复成功但活跃Run未退出，不提前发布Reward/结果");
                    That(retry.Count(packet=>packet is {Kind:1,Opcode:662,Body:[1]})==1,"幸存同Run成员662重试原封存候选后才ACK");
                    That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE instance_id=$p0 AND phase0_receipt IS NOT NULL",instance) is [[8L]],
                        "SQL重试仍提交原八人成员封存事实，断线不篡改候选");
                    string saved=fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY character_id,instance_id");
                    captain.Send(662,Body(symbol));
                    var replay=Collect(captain);
                    NoReward(replay,"SQL恢复后的重复662仍不能越过活跃Run退出屏障");
                    That(replay.Count(packet=>packet is {Kind:1,Opcode:662,Body:[1]})==1&&
                        fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY character_id,instance_id")==saved,"已提交662重复不重签资格");
                }
                else
                {
                    reporter.Send(662,Body(symbol));CheckCount(Collect(reporter),symbol,index+1);
                    captain.Send(662,Body(symbol));CheckCount(Collect(captain),symbol,index+1);
                }
            }
            if(dungeon!=5056)
            {
                MoveTo(first.Position);
                reporter.Send(662,Body(symbol));CheckCount(Collect(reporter),symbol,2);
            }
            List<WirePacket> returned=[];
            foreach(var peer in members.Where(peer=>peer!=captain))
            {
                var partial=Return(members,peer,true);
                foreach(var other in others)partial.AddRange(Drain(other,true));
                NoReward(partial,"非末成员退出仍无Reward/结果，不把任意一次Return当全部退出");
                if(dungeon!=5056)
                    That(partial.Where(packet=>packet.Opcode==583).All(packet=>Symbol(packet.Body,symbol)==2),"非末成员退出不撤销同Run设施贡献");
                returned.AddRange(partial);
            }
            returned.AddRange(Return(members,captain,true));
            foreach(var peer in others)returned.AddRange(Drain(peer,true));
            if(dungeon!=5056)
                That(returned.Any(packet=>packet.Opcode==583)&&returned.Where(packet=>packet.Opcode==583).Last() is var last&&Symbol(last.Body,symbol)==0,
                    "真实末成员Return回滚未清图设施贡献，重复进5054不能累积");
            if(dungeon==5056)
            {
                That(returned.Any(packet=>packet is {Kind:0,Opcode:587,Body:[3,0]})&&returned.Any(packet=>packet.Opcode==601),
                    "资格SQL及全部活跃Run退出后才发布Reward和阶段结果");
                var symbols=returned.Last(packet=>packet.Opcode==583).Body;
                That(Symbol(symbols,119)==2,"SQL失败/另一人重试/同房重放合计仍仅两个spread设施");
            }

            ushort[] Living()=>Enumerable.Range(0,map.Length==16?0:map[18]).Select(i=>19+i*22)
                .Where(at=>map[at+13]!=9&&map[at+14]==0).Select(at=>checked((ushort)at)).ToArray();
            void KillRoom()
            {
                foreach(int at in Living().OrderBy(at=>map[at+13]==3))
                {
                    captain.Send(39,FormalDungeonTcpTests.Death(U16(map,at+6),captain.Id,map[at+12]));
                    That(Collect(captain).Count(packet=>packet is {Kind:0,Opcode:38}&&U16(packet.Body)==U16(map,at+6))==members.Length,
                        "沿真实39清当前怪物并向本队逐人通知，不手改HP");
                }
                // 回访旧房使用16B缓存；当前完整29仅在测试本地标记已报告死亡，避免离开前重复报告。
                foreach(int at in Living())map[at+14]=1;
            }
            void LoadFacilityRoom()
            {
                foreach(var peer in members.SkipLast(1))
                {
                    peer.Send(37,new byte[8]);
                    That(!Drain(peer,true).Any(packet=>packet.Opcode==30),"末成员未加载不提前释放房间");
                }
                Rejected(reporter,Body(symbol),"本人已37但同队末成员未加载仍拒绝662");
                members[^1].Send(37,new byte[8]);
                var frames=Collect(members[^1]);
                That(frames.Count(packet=>packet is {Kind:1,Opcode:37,Body:[1]})==members.Length,"末成员37之后全员才恢复设施资格");
            }
            void MoveTo(DungeonPoint target)
            {
                var path=QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),target);
                foreach(var point in path.Skip(1))
                {
                    KillRoom();byte[] move=new byte[100];move[0]=(byte)point.X;move[1]=(byte)point.Y;
                    captain.Send(45,move);map=Drain(captain,true).Single(packet=>packet.Opcode==29).Body;
                    bool revisit=!visited.Add(point);
                    That(map[0]==point.X&&map[1]==point.Y&&map[2]==0&&
                        (revisit?map.Length==16&&map[13]==0&&map[14]==0&&map[15]==byte.MaxValue:map.Length>19&&map[13]==1),
                        "原29首次完整建房，已清旧格回访仅16B缓存，不再解析或重建怪物");
                    foreach(var peer in members.Where(peer=>peer!=captain))Bytes(Drain(peer,true).Single(packet=>packet.Opcode==29).Body,map,"原45四人相同场景");
                    foreach(var peer in others)Drain(peer,true);
                    Rejected(reporter,Body(symbol),"45新场景尚未全员37加载时拒绝662");
                    LoadFacilityRoom();
                }
            }
        }
        List<WirePacket> Collect(TcpPeer sender)=>new[]{sender}.Concat(members.Concat(others).Where(peer=>peer!=sender)).SelectMany(peer=>Drain(peer,true)).ToList();
        void Rejected(TcpPeer peer,byte[] body,string message)
        {
            peer.Send(662,body);var frames=Collect(peer);
            That(!frames.Any(packet=>packet.Opcode is 662 or 583 or 601),message);
        }
        static void NoReward(IEnumerable<WirePacket> frames,string message)=>
            That(!frames.Any(packet=>packet.Opcode==601||packet is {Kind:0,Opcode:587,Body:[3,0]}),message);
        static byte[] Body(int symbol,uint value=1,byte op=1)=>Build(writer=>{writer.Write((uint)symbol);writer.Write(value);writer.Write(op);});
        static uint Symbol(byte[] body,int symbol)
        {
            int at=Enumerable.Range(0,body[0]).Select(i=>1+i*8).Single(at=>U32(body,at)==symbol);
            return U32(body,at+4);
        }
        static void CheckCount(List<WirePacket> frames,int symbol,int expected)
        {
            That(frames.Count(packet=>packet is {Kind:1,Opcode:662,Body:[1]})==1,"原9B请求准确返回单一662 [01]，没有扩展opcode");
            var snapshots=frames.Where(packet=>packet.Opcode==583).ToArray();
            That(snapshots.Length==8&&snapshots.All(packet=>Symbol(packet.Body,symbol)==expected),"全八人完整583同步同一设施计数，跨成员重放不增量");
        }
    }
}
