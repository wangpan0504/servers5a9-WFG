using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class LukeRaidRunTcpTests
{
    // 真实Runtime/666/15/16/39/42/660；奖励/终结阈值为显式合成策略，不发送未解释600。
    internal static void Phases(TcpTestFixture fixture,TcpPeer[] peers,RaidScript raw,byte[] roster,RaidDungeonCatalog catalog,bool energyObjects=false)
    {
        var members=peers.Take(4).ToArray();var others=peers.Skip(4).ToArray();
        var captain=members.Single(peer=>peer.Id==Leader(roster));
        var instance=(string)fixture.Query("SELECT instance_id FROM character_raid_runs WHERE character_id=$p0 AND state=0",captain.Id)[0][0]!;
        for(byte phase=0;phase<2;phase++)
        {
            if(phase==1&&energyObjects)
                foreach(int energy in new[]{5063,5066})
                {
                    var entered=Enter(members,captain,energy);Load(members);
                    Clear(fixture,members,others,captain,entered[0],catalog,phase,
                        expectedHp:(raw.Symbols[raw.Phases[1].Dungeons.Single(row=>row.Id==energy).HpSymbol],0),
                        passiveTarget:energy==5063?64062:64060);
                    That(fixture.Query("SELECT count(*) FROM character_dungeon_scene_clear_receipts WHERE dungeon_id=$p0 AND quest_id=0",energy)
                        is [[4L]],"能源图队长及三名队员逐人保存真实无任务场景清图，不依赖普通46");
                    foreach(var peer in members.Where(peer=>peer!=captain))Return(members,peer,true,resultCommand:true);
                    Return(members,captain,true,resultCommand:true);
                }
            int dungeon=phase==0?5054:5311;
            var definition=raw.Phases[phase].Dungeons.Single(row=>row.Id==dungeon);
            for(int run=0;run<definition.HpRaw;run++)
            {
                var entered=Enter(members,captain,dungeon);Load(members);
                if(phase==1&&run==0) ManualCoins(fixture,members,others,phase,20);
                bool terminal=run+1==definition.HpRaw;
                var phaseResults=terminal?PhasePackets(peers):null;
                Clear(fixture,members,others,captain,entered[0],catalog,phase,phaseResults:phaseResults);
                string field=phase==0?"phase0":"phase1";
                That(fixture.Query($"SELECT count(*) FROM character_raid_runs WHERE instance_id=$p0 AND {field}_receipt IS NOT NULL",instance)
                    is [[long receipts]]&&receipts==(terminal?8:0),"仅权威HP归零终结一次签发全团阶段资格，非每角色/每Run提前结算");
                if(terminal)
                {
                    That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE instance_id=$p0 AND state=2",instance)
                        is [[long cleared]]&&cleared==(phase==1?8:0),"P1不占整团周clear；P2在领奖/回城前即持久计次");
                    That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE instance_id=$p0 AND phase=$p1",instance,phase)
                        is [[0L]],"阶段资格不是奖励receipt，不领取不能提前改资产");
                }
                List<WirePacket> result=[];
                if(terminal) result.AddRange(phaseResults!.Values.SelectMany(frames=>frames));
                else
                {
                    foreach(var peer in members.Where(peer=>peer!=captain))result.AddRange(Return(members,peer,true));
                    result.AddRange(Return(members,captain,true));
                    foreach(var peer in others)result.AddRange(Drain(peer,true));
                }
                if(terminal)
                {
                    That(result.Count(packet=>packet is {Kind:0,Opcode:601}&&packet.Body[1]==phase)==8,
                        "全部旧Run正式退出后，八名已登记成员各收到一份已证10B阶段结果");
                    That(result.Any(packet=>packet is {Kind:0,Opcode:587}&&packet.Body.SequenceEqual(new byte[]{3,phase})),
                        "阶段SQL成功与退出屏障之后才发布Reward状态");
                    var counts = result.Where(packet=>packet is {Kind:0,Opcode:1446}).ToArray();
                    That(counts.Length==(phase==1?8:0) && counts.All(packet=>RaidPanelTests.ReadWeeklyClears(packet.Body)[1]==(1u,1u)),
                        "卢克只在真实整团终结后向八名成员刷新日周已用数，首阶段不占通关数");
                }
                else That(!result.Any(packet=>packet.Opcode==601),"HP尚未归零不提前显示阶段结果");
            }
            foreach(var peer in peers)
            {
                foreach(byte invalid in new byte[]{2,5})
                {
                    string before=fixture.Snapshot();
                    peer.Send(660,[invalid,1]);
                    var rejected=Drain(peer,true);
                    That(rejected.Count(packet=>packet is {Kind:1,Opcode:660}&&packet.Body.SequenceEqual(new byte[]{0,0x12,0}))==1&&
                        !rejected.Any(packet=>packet.Opcode==600||packet is {Kind:1,Opcode:660,Body:[1]}),"未知2/5明确失败清pending，不发成功ACK或展示");
                    That(fixture.Snapshot()==before,"未知奖励类型不改变任何持久事实或资产");
                }
                // 匹配当前原生连续请求顺序，不把两种合法奖励合并成一份。
                peer.Send(660,[1,1]);peer.Send(660,[0,1]);
                var claimed=Drain(peer,true);
                That(claimed.Count(packet=>packet is {Kind:1,Opcode:660,Body:[1]})==2&&!claimed.Any(packet=>packet.Opcode==600),
                    "原生1→0双请求各自提交/ACK，无猜测600尾字段");
                That(!claimed.Any(packet=>packet.Opcode==601),"同Reward领奖通知不重播601结果演出");
                if(peer!=peers[^1])That(!claimed.Any(packet=>packet.Opcode==587),"尚未完成全阶段领取不重复587当前Reward");
                string saved=fixture.Snapshot();
                peer.Send(660,[0,1]);peer.Send(660,[1,1]);
                var replayed=Drain(peer,true);
                That(replayed.Count(packet=>packet is {Kind:1,Opcode:660,Body:[1]})==2&&fixture.Snapshot()==saved&&
                    !replayed.Any(packet=>packet.Opcode is 587 or 601),
                    "乱序重放沿各自原receipt恢复，不重复发奖/计次/阶段演出");
                var otherFrames=peers.Where(other=>other!=peer).Select(other=>Drain(other,true)).ToArray();
                if(phase==0&&peer==peers[^1])
                {
                    var recipients=otherFrames.Append(claimed.Concat(replayed).ToList()).ToArray();
                    That(recipients.Length==8&&recipients.All(rows=>rows.Any(packet=>packet is {Kind:0,Opcode:778}&&packet.Body.SequenceEqual(new byte[]{0}))),
                        "P1所有奖励确认后，全八人P2初始778为已用0，不是剩余20");
                    foreach(var rows in recipients)
                        That(!rows.Any(packet=>packet is {Kind:0,Opcode:778}&&packet.Body[0]!=0),"跨phase不能发布旧额度");
                }
            }
            That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE instance_id=$p0 AND phase=$p1 AND raid_type=1",instance,phase)
                is [[16L]],"每阶段八人两种奖项各一条，与Anton类型隔离");
            That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE instance_id=$p0 AND phase0_receipt IS NOT NULL",instance)
                is [[8L]],"进入下一阶段及最终领奖均不覆盖P1冻结资格");
        }
    }
}
