using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class LukeRaidRunTcpTests
{
    internal static async Task TimersAsync(TcpTestFixture fixture,TcpPeer[] peers)
    {
        var first=peers[..4]; var second=peers[4..];
        // 真实666已启用Runtime时钟；正式16安装5054触发SET，不调用领域Expire或Attach。
        var firstRows=Enter(first,first[0],5054);
        That(firstRows.All(frames=>frames.Any(packet=>packet is {Kind:0,Opcode:586} && packet.Body.Length==6 &&
            U32(packet.Body)==5056 && packet.Body[5]==(byte)RaidDungeonState.Close)), "SET时先关闭目标，不能把入场初态冒充到期开放");
        // 中间成员写入时故意失败，前面成员的阶段资格必须同事务回滚。
        fixture.Sql($"CREATE TRIGGER fail_timer_terminal BEFORE UPDATE ON character_raid_runs WHEN NEW.character_id={peers[3].Id} AND NEW.phase0_event IS NOT NULL BEGIN SELECT RAISE(ABORT,'timer sealed rollback'); END");
        var previousSecond = ReadOtherPartyDeparture(first, second);
        var secondRows=Enter(second,second[0],5055);
        // 保留已到达的全部阶段/自然时钟通知，继续参与原5056开放与失败回滚断言。
        for (int index = 0; index < secondRows.Length; index++)
            secondRows[index].InsertRange(0, previousSecond[index]);
        var rows=firstRows.Concat(secondRows).ToArray();
        for(int index=0;index<peers.Length;index++)
        {
            bool Opened(WirePacket packet) => packet is {Kind:0,Opcode:586} && packet.Body.Length==6 &&
                U32(packet.Body)==5056 && packet.Body[5]==(byte)RaidDungeonState.Open;
            if(!rows[index].Any(Opened))
            {
                bool found=false;
                for(int count=0;count<128;count++)
                {
                    var packet=peers[index].Read(); Validate(packet); rows[index].Add(packet);
                    if(Opened(packet)){found=true;break;}
                }
                That(found,"自然时钟在有界通知预算内向两队发出5056开放状态");
            }
            That(rows[index].Any(Opened)&&!rows[index].Any(p=>p.Opcode is 587 or 597 or 600),
                "自然到期发布DGN状态及已证按图596，不重播阶段动画或奖励预览");
        }
        Load(first); Load(second);
        var waiting=System.Diagnostics.Stopwatch.StartNew();bool faulted=false;
        while(waiting.Elapsed<TimeSpan.FromSeconds(10))
        {
            try
            {
                faulted=fixture.Events().Any(row=>row.GetProperty("Kind").GetString()=="raid_timer_failed" &&
                    row.GetProperty("Detail").GetProperty("Stage").GetString()=="settlement-fault" &&
                    row.GetProperty("Detail").GetProperty("Message").GetString()!.Contains("timer sealed rollback",StringComparison.Ordinal));
            }
            catch(System.Text.Json.JsonException) { }
            if(faulted)break;
            await Task.Delay(10);
        }
        That(faulted,"自然6秒终结经真实阶段SQL中段失败进入明确fault，而非人工标记或静默等待");
        That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE phase0_receipt IS NOT NULL") is [[0L]],
            "真实中段SQL失败不保留前面成员的半份资格");
        foreach(var peer in first)Return(peers,peer);
        foreach(var peer in second)Return(peers,peer);
        foreach(var peer in peers)
        {
            peer.Send(660,[0,1]);
            That(Drain(peer,true).Any(packet=>packet is {Kind:1,Opcode:660,Body:[0,0x12,0]}),
                "fault后42完整回城且连接可继续，不能领取或重入损坏结算");
        }
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[0L]],"fault不伪造奖励或补发资产");
        fixture.Sql("DROP TRIGGER fail_timer_terminal");
    }
}
