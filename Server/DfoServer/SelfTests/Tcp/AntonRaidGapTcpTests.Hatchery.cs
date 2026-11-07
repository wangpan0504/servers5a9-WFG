using System.Buffers.Binary;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidGapTcpTests
{
    static void VerifyHatcheryTransfer(TcpTestFixture fixture, TcpPeer leader, TcpPeer member,
        RaidDungeonCatalog catalog, LukeRaidClockTests.Clock clock)
    {
        clock.Advance(180);
        var opened = AntonTcpProbe.Barrier(leader, "内部入口自然开放");
        AntonTcpProbe.Barrier(member, "内部入口队员开放");
        var targets = opened.Where(packet => packet is { Kind: 0, Opcode: 586, Body.Length: 6 } && packet.Body[5] == 0)
            .Select(packet => U32(packet.Body)).Where(id => id is >= 221 and <= 224).ToHashSet();
        That(targets.Count == 3, "内部转场仍遵循180秒四选三");
        var leadReport = new AntonTcpProbe.AntonProbeReport();
        var memberReport = new AntonTcpProbe.AntonProbeReport();
        foreach (uint target in new uint[] { 221, 222, 223, 224 })
        {
            leader.Send(15, BitConverter.GetBytes(218u));
            AntonTcpProbe.Barrier(leader, "转场选择218"); AntonTcpProbe.Barrier(member, "队员选择218");
            leader.Send(16, Select(218));
            var entry = AntonTcpProbe.Barrier(leader, "转场源218");
            var other = AntonTcpProbe.Barrier(member, "队员转场源218");
            byte[] info = entry.Single(packet => packet is { Kind: 0, Opcode: 28 }).Body;
            byte[] map = entry.Last(packet => packet is { Kind: 0, Opcode: 29 }).Body;
            SameRoom(map, other.Last(packet => packet is { Kind: 0, Opcode: 29 }).Body, null, "转场源实例共享");
            Load();
            string initial = fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id");
            Reject(leader, target, "非四角房间不能借16跨图");
            That(initial == fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id"), "错误房间不消耗新尝试号");
            (int x, int y, int id) = target switch
            {
                221 => (3, 0, 19103), 222 => (3, 4, 19119), 223 => (0, 4, 19116), _ => (0, 0, 19100)
            };
            var route = AntonTcpProbe.Route(catalog, 218, info[7], map[0], map[1], checked((byte)x), checked((byte)y), avoidBossRooms: true);
            foreach (var next in route.Skip(1))
            {
                AntonTcpProbe.ClearRoom(leader, map, leadReport, out _, out _, false, member, memberReport);
                var move = new byte[100]; move[0] = checked((byte)next.X); move[1] = checked((byte)next.Y);
                leader.Send(45, move);
                map = AntonTcpProbe.Barrier(leader, "四角实际45走图").Last(packet => packet is { Kind: 0, Opcode: 29 }).Body;
                AntonTcpProbe.Barrier(member, "队员四角走图");
                Load();
            }
            That(U32(map, 14) == id, "转场源由正式29实际MAP匹配");
            Reject(member, target, "队员不能替队长签发整队转场");
            Reject(leader, target == 221 ? 222u : 221u, "角落与目标不匹配时保留原场景");
            if (!targets.Contains(target))
                Reject(leader, target, "四选三未开放目标不能从内部进入");
            else
            {
                leader.Send(16, Select(target));
                var changed = AntonTcpProbe.Barrier(leader, "原生16内部转场");
                var memberChanged = AntonTcpProbe.Barrier(member, "队员内部转场");
                foreach (var rows in new[] { changed, memberChanged })
                {
                    int transition = rows.FindIndex(packet => packet is { Kind: 0, Opcode: 27, Body.Length: 42 });
                    int information = rows.FindIndex(packet => packet is { Kind: 0, Opcode: 28 } && U32(packet.Body) == target);
                    That(transition >= 0 && information > transition && rows.Any(packet => packet is { Kind: 0, Opcode: 29 }) &&
                        rows.All(packet => packet.Opcode is not (24 or 601)), "整队27/28/29原子转场，不回城或制造通关");
                }
                SameRoom(changed.Last(packet => packet.Opcode == 29).Body, memberChanged.Last(packet => packet.Opcode == 29).Body,
                    null, "目标共享物理房间");
                string installed = fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id");
                leader.Send(16, Select(218));
                That(AntonTcpProbe.Barrier(leader, "迟到源16不能替换Loading目标").All(packet => packet.Opcode is not (28 or 29)),
                    "Loading拒绝迟到的源图请求");
                That(installed == fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id"), "迟到16不签发第二个Run");
                leader.Send(37, new byte[8]);
                That(AntonTcpProbe.Barrier(leader, "目标等待队员").All(packet => packet.Opcode is not (30 or 37)), "新实例保留整队加载屏障");
                member.Send(37, new byte[8]);
                AntonTcpProbe.Barrier(member, "目标队员完成加载");
                That(AntonTcpProbe.Barrier(leader, "目标全员完成加载").Any(packet => packet is { Kind: 1, Opcode: 37, Body: [1] }),
                    "目标全员加载后才释放37");
                Reject(leader, 221, "孵化场不能重用218内部转场能力");
            }
            foreach (var peer in new[] { leader, member })
            {
                peer.Send(42); AntonTcpProbe.Barrier(peer, "内部转场用例退场");
            }

            void Load()
            {
                leader.Send(37, new byte[8]); member.Send(37, new byte[8]);
                AntonTcpProbe.Barrier(member, "四角队员加载"); AntonTcpProbe.Barrier(leader, "四角全员加载");
            }
        }

        void Reject(TcpPeer peer, uint target, string reason)
        {
            peer.Send(16, Select(target));
            var rows = AntonTcpProbe.Barrier(peer, reason);
            That(rows.All(packet => packet.Opcode is not (27 or 28 or 29 or 24)), reason);
            var other = ReferenceEquals(peer, leader) ? member : leader;
            That(AntonTcpProbe.Barrier(other, reason + "队友").All(packet => packet.Opcode is not (27 or 28 or 29 or 24)), "拒绝不改变队友场景");
        }
        static byte[] Select(uint target)
        {
            byte[] body = AntonTcpProbe.LiveSelect210.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(body, target); return body;
        }
    }
}
