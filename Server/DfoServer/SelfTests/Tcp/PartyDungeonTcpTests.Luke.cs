using System.Buffers.Binary;
using DfoServer.Game.Quests;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    static async Task RunLukeAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        await using var fixture=new TcpTestFixture(source,Path.Combine(root,"party-luke-daily"),resources:resources);
        await fixture.StartAsync();
        var (position,done)=OrdinaryLukePartyTests.Entrance(resources);
        var quest=resources.Quests.Get(2902);
        using var group=new Coop(fixture,resources,"lukeday",position,done.Concat(quest.Prerequisites).Distinct().ToArray(),95,2);
        foreach(var peer in group.Peers)
        {
            peer.Send(31,[31,0,0x56,0x0b]);
            That(Drain(peer).Any(p=>p is {Kind:1,Opcode:31} && p.Body[0]==1),"两名角色分别真实接取2902，不由队长代接");
        }
        var alpha=group.Peers[0]; var beta=group.Peers[1];
        int day=QuestCycle.Day(DateTimeOffset.UtcNow);
        fixture.Sql("INSERT INTO character_luke_normal_progress VALUES($p0,$p1,$p2,1)",alpha.Id,"lukeday0",day);
        group.Join(); group.Open();
        for (int member = 0; member < group.Peers.Length; member++)
        {
            group.Peers[member].Send(871,[42,0,0,0]);
            byte[] info = group.Peers[member].Expect(0,602).Body;
            That(info.Length == 16 && U32(info) == 42 && info[4] == (member == 0 ? 1 : 0) && U32(info,5) == (member == 0 ? 1u : 0u),
                "组队选图查询分别读取队长/队员进度，不广播队长资格");
        }
        string before=fixture.Snapshot();
        alpha.Send(16,Coop.Selection(5102,0,2));
        That(group.All()[0].Any(p=>p is {Kind:1,Opcode:16,Body:[0,9,0,0]}) && fixture.Snapshot()==before,
            "TCP队长已解锁而队员未解锁，整队原拒绝响应且资产/进度不变");
        foreach(int monster in resources.ItemResources.DungeonSpecialDrops.OrdinaryLukeCards.Keys)
            fixture.Sql("INSERT INTO character_luke_normal_drop_claims VALUES($p0,$p1,$p2,$p3,$p4,$p5)",
                alpha.Id,"lukeday0",day,monster,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"));
        alpha.Send(16,Coop.Selection(5101,0,2));
        var entries = group.All();
        for (int member = 0; member < entries.Length; member++)
        {
            var info = entries[member].Single(p=>p is {Kind:0,Opcode:602}).Body;
            That(info[4] == (member == 0 ? 1 : 0) && U32(info,5) == (member == 0 ? 1u : 0u),
                "组队进入场景前逐人同步自己的序列缓存");
        }
        group.ReadEntry(entries); group.Load();
        var maze=SelfTestCatalogs.Dungeons(resources.Archive).Get(5101).Mazes.Single();
        var route=QuestStoryRouteTests.Path(maze.StaticTopology,maze.Starts.Single(),maze.Bosses.Single());
        int checkedSources=0,pickups=0,questSources=0;
        foreach(var point in route)
        {
            byte[] map=group.Map;
            foreach(int at in Enumerable.Range(0,map[18]).Select(i=>19+22*i).Where(at=>map[at+21]==0 && map[at+13]!=9))
            {
                ushort key=U16(map,at+6); int monster=checked((int)U32(map,at+8));
                byte[] report=FormalDungeonTcpTests.Death(key,alpha.Id,map[at+12]);
                alpha.Send(39,report); var rows=group.All();
                var deaths=rows.Select(r=>r.Single(p=>p is {Kind:0,Opcode:38} && U16(p.Body)==key).Body).ToArray();
                if(resources.ItemResources.DungeonSpecialDrops.OrdinaryLukeCards.ContainsKey(monster))
                {
                    checkedSources++;
                    That(U16(deaths[0],2)==0 && fixture.Query("SELECT count(*) FROM character_luke_normal_drop_claims WHERE character_id=$p0 AND day=$p1 AND source_monster_id=$p2",beta.Id,day,monster) is [[1L]],
                        "真实死亡包已领队长无正常地面，未领队员仍独立写首击资格");
                    var questItems=quest.Definition!.Drops.Where(row=>row.DungeonId==5101 && row.TemplateId==monster).Select(row=>row.ItemId).Distinct().ToArray();
                    if(questItems.Length>0)
                    {
                        questSources++;
                        foreach(var peer in group.Peers)
                            That(fixture.Query("SELECT count(*) FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid) WHERE s.character_id=$p0 AND json_extract(i.item_core,'$.ItemId') IN ("+string.Join(',',questItems)+")",peer.Id)
                                is [[long amount]] && amount>0,"正常资格已用的角色仍实际获得自己的2902目标材料，队友也独立获得");
                    }
                }
                string committed=fixture.Snapshot();
                beta.Send(39,report);
                That(group.All(beta).All(r=>!r.Any(p=>p is {Kind:0,Opcode:38})) && fixture.Snapshot()==committed,
                    "另一队员重复死亡通知不增发、不重抽、不改日资格");
                for(int member=0;member<group.Peers.Length;member++)
                for(int ordinal=0;ordinal<U16(deaths[member],2);ordinal++)
                {
                    uint ground=U32(deaths[member],4+131*ordinal);
                    byte[] pickup=new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(pickup,ground);
                    group.Peers[member].Send(43,pickup); var result=group.All(group.Peers[member]);
                    That(result[member].Any(p=>p is {Kind:1,Opcode:43,Body:[1]}) &&
                        result[member].Any(p=>p is {Kind:0,Opcode:39} && U32(p.Body)==ground) &&
                        !result[1-member].Any(p=>p is {Kind:0,Opcode:39}),"日资格限制不改变每人独立地面与真实拾取归属");
                    pickups++;
                }
            }
            if(point==route[^1]) break;
            var next=route[Array.IndexOf(route,point)+1]; group.Move((byte)next.X,(byte)next.Y); group.Load();
        }
        That(checkedSources>0 && pickups>0 && questSources>0,$"真实5101覆盖奖励源怪、任务材料与未受限玩家地面拾取：sources={checkedSources},quest={questSources},pickups={pickups}");
        Console.WriteLine($"INFO 卢克组队真实TCP source={checkedSources}, quest2902={questSources}, pickups={pickups}");
        group.Settle();
        That(group.Peers.All(peer=>fixture.Query("SELECT day,progress FROM character_luke_normal_progress WHERE character_id=$p0",peer.Id)
            is [[long savedDay,1L]] && savedDay==day),"两人各自真实结算，只推进共同入场日；重复早图不降队长进度");
    }
}
