using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.AntonRaidTcpTests;

namespace DfoServer.Testing;

internal static partial class RaidChannelLandingTcpTests
{
    internal static async Task RunNpcTravelAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        static void Drain(TcpPeer peer) => AntonTcpProbe.Barrier(peer, "raid NPC travel");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "raid-npc-travel"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        var quests = RaidQuests.Concat(resources.Towns.SpecialChannelQuests.Values).ToHashSet();
        foreach (var (type, channel, guide, town, area, recruitNpc, waitingNpc) in new[]
        {
            (0, 19, false, 19, 2, 326u, 327u), (1, 20, false, 32, 4, 458u, 462u),
            (2, 39, false, 57, 2, 624u, 625u), (2, 39, true, 57, 33, 624u, 625u),
            (3, 42, false, 59, 2, 20478u, 20479u), (3, 42, true, 59, 2, 20478u, 20479u)
        })
        {
            var route = resources.Towns.RaidNpcRoutes.Single(row => row.Type == type);
            That(route.RecruitNpc == recruitNpc && route.WaitingNpc == waitingNpc && route.Room(guide) is { } room &&
                room.Town == town && room.Area == area, "四团普通/引导的NPC及房间来自实际候选PVF");
            var recruitArea = resources.Towns.Areas.Values.Single(row => row.Map(quests).Npcs.Any(n => n.Id == recruitNpc));
            // 每模式使用独立账号，走正式频道出生和房间出口，不复用上一轮的换频道快照。
            string account = $"raidnpc{type}{(guide ? 1 : 0)}";
            var character = CreateRaider(fixture, resources, $"RaidNpc{type}{(guide ? 1 : 0)}", account: account);
            using var peer = Open(fixture, account, gamePort: endpoints[(byte)channel], channelNumber: (byte)channel);
            peer.Select();
            Drain(peer);
            Location Position()
            {
                var row = fixture.Query("SELECT town,area,x,y,direction FROM characters WHERE id=$p0", peer.Id).Single();
                return new(Convert.ToByte(row[0]), Convert.ToByte(row[1]), Convert.ToUInt16(row[2]), Convert.ToUInt16(row[3]), Convert.ToByte(row[4]));
            }
            if (resources.Towns.IsRoom(Position(), quests)) { peer.Send(1426); Drain(peer); }
            if (Position().Town != recruitArea.Town || Position().Area != recruitArea.Area)
            {
                var origin = Position();
                var gate = resources.Towns.Entrance(origin, new(recruitArea.Town, recruitArea.Area, 0, 0), quests)!;
                That(gate is not null, "安徒恩房间出门后沿真实门进入招募街区");
                peer.Send(36, Area(gate!.Town, gate.Area, gate.X, gate.Y, origin.Town, origin.Area));
                Drain(peer);
            }
            var recruitment = Position();
            That(recruitment.Town == recruitArea.Town && recruitment.Area == recruitArea.Area, "实际连接已到达对应创建攻坚队NPC街区");
            var target = resources.Towns.Areas[((byte)town, (byte)area)].Map(quests).Npcs.Single(n => n.Id == waitingNpc);
            byte[] Enter(byte targetArea) => Area((byte)town, targetArea, (ushort)target.X, checked((ushort)(target.Y + 30)),
                recruitment.Town, recruitment.Area, npc: waitingNpc);
            string before = fixture.Snapshot();
            peer.Send(36, Enter((byte)area));
            Bytes(Until(peer, 1, 36).Body, [0, 1, 0, (byte)town, (byte)area], "无攻坚队时确认菜单有失败收尾");
            Drain(peer);
            That(fixture.Snapshot() == before, "未入团拒绝不修改数据库和revision");
            peer.Send(664, Build(writer => { writer.Write((byte)type); writer.Write(Name("Npc")); writer.Write(new byte[] { guide ? (byte)1 : (byte)0, 0, 0, 7 }); }));
            That(Until(peer, 1, 664).Body is [1, ..], "通过正式请求创建对应普通或引导攻坚队");
            Drain(peer);
            if (type == 2)
            {
                byte wrong = (byte)(guide ? 2 : 33);
                before = fixture.Snapshot();
                peer.Send(36, Enter(wrong));
                Bytes(Until(peer, 1, 36).Body, [0, 1, 0, (byte)town, wrong], "普通与引导候战房间不能混用");
                Drain(peer);
                That(fixture.Snapshot() == before, "跨模式拒绝零提交");
            }
            string returns = fixture.Snapshot("SELECT return_town,return_area,return_x,return_y,return_direction FROM characters WHERE id=$p0", peer.Id);
            var revision = fixture.Query("SELECT revision FROM characters WHERE id=$p0", peer.Id)[0][0];
            peer.Send(36, Enter((byte)area));
            var arrival = Until(peer, 0, 23);
            That(arrival.Body[2] == town && arrival.Body[3] == area, "菜单确认的23到达对应副本入口房间");
            Until(peer, 0, 24);
            Drain(peer);
            That(Position() is { } landed && landed.Town == town && landed.Area == area && landed.X == target.X && landed.Y == target.Y + 30,
                "实际存档使用候战NPC落点，保留请求坐标");
            That(Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE id=$p0", peer.Id)[0][0]) == Convert.ToInt64(revision) + 1 &&
                fixture.Snapshot("SELECT return_town,return_area,return_x,return_y,return_direction FROM characters WHERE id=$p0", peer.Id) == returns,
                "单次位置事务并保留普通返回地区");
            That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[0L]], "只传送候战，不自动开团或扣票");
            // 同一菜单请求重放来源已经失效，不得再提交位置。
            before = fixture.Snapshot();
            peer.Send(36, Enter((byte)area));
            Drain(peer);
            That(fixture.Snapshot() == before, "重复确认不产生第二笔保存");
            Console.WriteLine($"PASS raid NPC type={type} guide={guide} destination={town}/{area}");
        }
        await fixture.StopAsync();
    }
}
