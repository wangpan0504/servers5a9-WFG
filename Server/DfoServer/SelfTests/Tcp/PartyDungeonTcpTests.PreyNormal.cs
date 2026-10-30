using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    static async Task RunPreyNormalAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source,Path.Combine(root,"party-prey-normal"),resources:resources);
        await fixture.StartAsync();
        var area = resources.Towns.Areas[((byte)118,(byte)0)];
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Append(8925).ToHashSet();
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new DfoServer.GameWorld.Towns.Location(118,0,(ushort)(bounds.X+bounds.Width/2),(ushort)(bounds.Y+bounds.Height/2));
        using var group = new Coop(fixture,resources,"preynorm",position,done.ToArray(),95,2);
        var alpha = group.Peers[0]; var beta = group.Peers[1];
        int day = QuestCycle.Day(DateTimeOffset.UtcNow), branch = PreyNormalCatalog.BranchForDay(day);
        fixture.Sql("INSERT INTO character_prey_normal_progress VALUES($p0,'preynorm0',$p1,$p2,4)",alpha.Id,day,branch);
        group.Join(); group.Open();
        foreach (var peer in group.Peers)
        {
            peer.Send(871,[203,0,0,0]);
            byte[] info = peer.Expect(0,602).Body;
            That(info.Length == 16 && U32(info) == 203 && U32(info,9) == branch && info[4] == (peer == alpha ? 4 : 0),
                "组队查询各自进度，不能借队长前序");
        }
        string before = fixture.Snapshot();
        alpha.Send(16,Coop.Selection(400000046,0,2));
        That(group.All()[0].Any(packet => packet is { Kind:1,Opcode:16,Body:[0,9,0,0] }) && fixture.Snapshot() == before,
            "队员未完成前序则整队不扣疲劳、不建立尝试");
        fixture.Sql("INSERT INTO character_prey_normal_progress VALUES($p0,'preynorm1',$p1,$p2,4)",beta.Id,day,branch);
        alpha.Send(16,Coop.Selection(400000046,0,2)); group.ReadEntry(group.All()); group.Load();
        That(group.LastPackets.All(rows => rows.Any(packet => packet is { Kind:0,Opcode:30 })),"全员加载后释放本体首房");
        group.Clear(); group.Move(1,0); group.Load();
        That(U32(group.Map,14) == 400000102,"本体终点使用原102地图，不替换fake图");
        group.Clear(); group.Settle();
        foreach (var peer in group.Peers)
        {
            That(fixture.Query("SELECT day,branch,progress FROM character_prey_normal_progress WHERE character_id=$p0",peer.Id)
                is [[long savedDay,long savedBranch,5L]] && savedDay == day && savedBranch == branch,"两名成员真实结算分别持久化五图进度");
            peer.Send(69); group.All(peer); peer.Send(70); group.All(peer); peer.Send(71,[0,0]); group.All(peer);
        }
        alpha.Send(72,[1,1]);
        var returned = group.All();
        That(returned.All(rows => rows.Any(packet => packet is { Kind:1,Opcode:72,Body:[1,1,2] }) &&
            rows.Any(packet => packet is { Kind:0,Opcode:24 })),"组队本体下一图统一回城并释放两人的结算占用");
        That(fixture.Query("SELECT count(*) FROM characters WHERE town=118 AND area=0") is [[2L]],"两人都返回普通等候区");
    }
}
