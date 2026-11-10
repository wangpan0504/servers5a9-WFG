using DfoServer.Game.Dungeons;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    internal static void VerifyParticularHell(TcpTestFixture fixture, TcpTestResources resources, DateTimeOffset now)
    {
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var definition = dungeons.Get(316);
        var required = definition.RequiredItem!;
        var (done, position) = DungeonEntryCostTests.Selection(resources.Towns, 316);
        done.UnionWith(resources.Quests.All.Select(quest => quest.Id));
        byte difficulty = definition.TryGetDesignatedDifficulty(out byte designated) ? designated : (byte)0;
        using var group = new Coop(fixture, resources, "particular", position, [.. done], 95, 2,
            rewards: [new(required.ItemId, required.Count * 5)]);
        var period = DungeonIncountPolicy.Period(now);
        int completedRounds = 0;
        for (int member = 0; member < group.Peers.Length; member++)
            fixture.Sql("INSERT INTO character_dungeon_incount(character_id,account,dungeon,day,daily_used,week,weekly_used) VALUES($p0,$p1,316,$p2,$p3,$p4,0)",
                group.Peers[member].Id, "particular" + member, period.Day, member == 0 ? 1 : 3, period.Week);
        group.Join(); group.Open();
        string before = fixture.Snapshot();
        group.Peers[0].Send(16, Coop.Selection(316, 0, difficulty));
        var rejected = group.All();
        That(rejected[0].Any(packet => packet is { Kind: 1, Opcode: 16, Body: [0, 9, 0, 0] }) &&
            rejected.All(rows => rows.All(packet => packet.Opcode is not (28 or 29))) && fixture.Snapshot() == before,
            "316队员次数已满时整队拒绝，未给队长扣材料、疲劳、次数或尝试号");
        fixture.Sql("UPDATE character_dungeon_incount SET daily_used=1 WHERE character_id=$p0 AND dungeon=316", group.Peers[1].Id);
        group.Peers[0].Send(16, Coop.Selection(316, 0, difficulty));
        var entry = group.All(); group.ReadEntry(entry); Counts(entry, 2, 4);
        before = fixture.Snapshot();
        group.Peers[0].Send(16, Coop.Selection(316, 0, difficulty)); group.All();
        That(fixture.Snapshot() == before, "316组队加载中重复入场不会再次扣全队材料与次数");
        Finish();
        group.Peers[0].Send(72, [1, 0]);
        var retry = group.All(); group.ReadEntry(retry); Counts(retry, 3, 3);
        That(retry.All(rows => rows.Any(packet => packet is { Kind: 1, Opcode: 72, Body: [1, 1, 0] })),
            "316组队F10成功明确确认结果页命令");
        Finish();
        var assets = group.Peers.Select(peer => (Used(peer.Id), Materials(peer.Id))).ToArray();
        group.Peers[0].Send(72, [1, 0]);
        var returned = group.All();
        That(returned.All(rows => rows.Any(packet => packet is { Kind: 1, Opcode: 72, Body: [1, 1, 2] }) &&
                rows.Any(packet => packet is { Kind: 0, Opcode: 24 }) && rows.All(packet => packet.Opcode is not (28 or 29))) &&
            group.Peers.Select(peer => (Used(peer.Id), Materials(peer.Id))).SequenceEqual(assets),
            "316组队F10耗尽明确回城，不挂结果页、不重扣或返还已用材料次数");
        That(group.Peers.All(peer => peer.ParticularHellUsed == 3), "316组队F10耗尽回城保持最后已发布的0/3，不伪造返还次数");

        void Counts(WirePacket[][] packets, long used, int portions)
        {
            for (int member = 0; member < group.Peers.Length; member++)
            {
                var pair = packets[member].Where(packet => packet.Opcode is 832 or 833).ToArray();
                That(pair is [{ Kind: 0, Opcode: 832, Body.Length: 264 }, { Kind: 0, Opcode: 833, Body.Length: 8 }] &&
                    Used(group.Peers[member].Id) == used && Materials(group.Peers[member].Id) == required.Count * portions,
                    "316组队入场或F10按全队同事务各扣一次材料并以832/833投影已提交次数");
                foreach (var packet in pair) group.Peers[member].ValidateServiceDayNotice(packet);
            }
        }
        void Finish()
        {
            group.Load();
            var maze = definition.Mazes.Single(row => row.SourceOrdinal == group.Information[7]);
            var route = QuestStoryRouteTests.Path(maze.StaticTopology, new(group.Map[0], group.Map[1]),
                new(group.Information[8], group.Information[9]));
            for (int index = 0; index < route.Length; index++)
            {
                group.Clear();
                if (index + 1 < route.Length)
                {
                    group.Move((byte)route[index + 1].X, (byte)route[index + 1].Y); group.Load();
                }
            }
            var report = DungeonSettlementProtocolTests.Report(group.Peers.Select(peer => (peer.Id, (byte)99)).ToArray());
            foreach (var peer in group.Peers)
            {
                peer.Send(46, report);
                That(Drain(peer).Any(packet => packet is { Kind: 0, Opcode: 34 }), "316组队F10前每名成员真实完成本轮结算");
            }
            completedRounds++;
            That(fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id IN ($p0,$p1)",
                    group.Peers[0].Id, group.Peers[1].Id) is [[long count]] && count == completedRounds * group.Peers.Length,
                "316组队两轮F10通关累计回执分别精确为2和4，不漏记或重复结算");
            group.All();
        }
        long Used(ushort id) => Convert.ToInt64(fixture.Query(
            "SELECT daily_used FROM character_dungeon_incount WHERE character_id=$p0 AND dungeon=316", id).Single()[0]);
        long Materials(ushort id) => Convert.ToInt64(fixture.Query(
            "SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1",
            id, required.ItemId).Single()[0]);
    }
}
