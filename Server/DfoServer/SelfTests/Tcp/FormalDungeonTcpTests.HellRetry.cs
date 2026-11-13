using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    static void VerifySealRetry(TcpTestFixture fixture,TcpPeer peer,TcpTestResources resources,byte[] information,byte[] map)
    {
        var dungeon = SelfTestCatalogs.Dungeons(resources.Archive).Get(291100308);
        for (int round = 0; round < 2; round++)
        {
            var maze = dungeon.Mazes.Single(row => row.SourceOrdinal == information[7]);
            var path = QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),new(information[8],information[9]));
            int permits = 0;
            for (int room = 0; room < path.Length; room++)
            {
                if (room > 0)
                {
                    var move = new byte[100]; move[0] = (byte)path[room].X; move[1] = (byte)path[room].Y;
                    peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                    PartyDungeonTcpTests.Drain(peer);
                    Load(peer,fatigue:false); peer.Expect(0,36); PartyDungeonTcpTests.Drain(peer);
                }
                // 最短路线穿过密门时也击杀真实波次；类型9特殊父对象不充当怪物死亡。
                foreach (int offset in Enumerable.Range(0,map[18]).Select(i => 19+22*i)
                    .Where(i => map[i+21] == 0 && map[i+13] != 9).OrderBy(i => map[i+13] == 3))
                {
                    peer.Send(39,Death(U16(map,offset+6),peer.Id,map[offset+12]));
                    permits += PartyDungeonTcpTests.Drain(peer).Count(packet => packet is { Kind:0,Opcode:31 });
                }
            }
            That(permits == 1,"深渊重试TCP每场真实Boss死亡仅发布一次结算许可");
            peer.Send(46,DungeonSettlementProtocolTests.Report(peer.Id,99));
            var settlement = PartyDungeonTcpTests.Drain(peer);
            That(settlement.Any(packet => packet is { Kind:0,Opcode:34 }) && fixture.Query(
                "SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[long count]] && count == round+1,
                "深渊重试TCP真实46提交独立通关回执");
            peer.Send(72,[1,0]);
            peer.Expect(1,72,round == 0 ? [1,1,0] : [1,1,2]);
            var notices = PartyDungeonTcpTests.Drain(peer);
            if (round == 0)
            {
                information = notices.Single(packet => packet is { Kind:0,Opcode:28 }).Body;
                map = notices.Single(packet => packet is { Kind:0,Opcode:29 }).Body;
                That(U32(information,0) == 291100308 && U16(information,14) == 1 && map[7] == 1 && fixture.Query(
                    "SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=3330",peer.Id) is [[0L]],
                    "真实72再次挑战保留深渊模式，第二次入场真实扣完余下30张邀请函");
                Load(peer,fatigue:false); peer.Expect(0,36); PartyDungeonTcpTests.Drain(peer);
            }
            else That(notices.Any(packet => packet is { Kind:0,Opcode:24 }) &&
                notices.All(packet => packet.Opcode is not (28 or 29)),
                "深渊票不足回复ACK72 option2并回城，不换普通图、不让结算页继续等ACK");
        }
    }
}
