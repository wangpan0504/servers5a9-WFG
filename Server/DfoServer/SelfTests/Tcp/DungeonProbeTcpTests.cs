using DfoServer.Development.Dungeons;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class DungeonProbeTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source with
        {
            DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.FinishLoadingMonsters
        }, Path.Combine(root, "dungeon-probe"), resources: resources);
        await fixture.StartAsync();
        using var peer = Open(fixture);
        peer.Create("ProbeLayers");
        ushort previousKey = 0;
        for (int selection = 0; selection < 2; selection++)
        {
            peer.Select();
            if (selection == 0)
            {
                peer.Send(31, Convert.FromHexString("1F00490C"));
                peer.Expect(1, 31);
                peer.Expect(0, 21);
            }
            peer.Travel(38, 0, 622, 196, 38, 1, bottom: true);
            peer.Travel(38, 2, 1085, 249, 38, 0, kind: 5);
            string before = fixture.Snapshot();
            peer.Send(15, new byte[4]);
            peer.Expect(1, 15, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
            That(peer.Expect(0, 27).Body.Length == 42, "diagnostic mode shares the normal selection entry");
            byte[] request = Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000");
            peer.Send(16, request);
            That(peer.Expect(0, 28).Body.Length == 36, "explicit runtime mode sends the information candidate");
            byte[] map = peer.Expect(0, 29).Body;
            That(map.Length == 111 && map[18] == 4, "monster diagnostic module sends four complete rows");
            ushort firstKey = U16(map, 25);
            That(firstKey > previousKey, "reselection does not reuse diagnostic object keys on the connection");
            previousKey = U16(map, 25 + 3 * 22);
            peer.Send(132);
            peer.Barrier();
            That(fixture.Snapshot() == before, "选图132不能越过已开始的副本诊断活动");
            peer.Send(37, new byte[8]);
            peer.Expect(1, 37, [1]);
            peer.Expect(0, 30, new byte[5]);
            peer.Expect(0, 3, Build(writer =>
            {
                writer.Write((byte)1);
                writer.Write(peer.Id);
                writer.Write((byte)1);
            }));
            // 技能包只记录摘要状态；正常、篡改及未知宽度都不能凭此发死亡/经验或关闭连接。
            foreach (string hex in new[]
            {
                "002E00D00729000001004AADA701", "00050070172900000100D260CC5C",
                "00050070172900000100D260CC5D", "00050070172900000100", "0005007017290000010000"
            })
                peer.Send(38, Convert.FromHexString(hex));
            peer.Barrier();
            That(fixture.Snapshot() == before, "skill checksum observation never awards assets or confirms a death");
            peer.Send(35, Move(1000, 249));
            peer.Barrier();
            That(fixture.Snapshot() == before, "activity gate prevents dungeon movement becoming a town save");
            if (selection == 0)
            {
                // 直接从活动中返回选角，验证协议生命周期会清理诊断状态。
                peer.Send(7);
                peer.Expect(1, 7, [1]);
                peer.Expect(0, 2);
            }
            else
            {
                peer.Send(42);
                peer.Expect(1, 42, [1]);
                peer.Expect(0, 24);
                peer.Send(132);
                peer.Barrier();
                peer.Send(16, request);
                peer.Barrier();
                That(fixture.Snapshot() == before, "return does not restore same-selection probe quota or write assets");
                peer.Send(35, Move(1000, 249));
                peer.Barrier();
                That(fixture.Snapshot() != before, "successful return releases the activity gate for town movement");
            }
        }
        await fixture.StopAsync();
        string[] skillDetails = fixture.Events()
            .Where(entry => entry.GetProperty("Kind").GetString() == "client_notice")
            .Select(entry => entry.GetProperty("Detail"))
            .Where(detail => detail.GetProperty("Action").GetString() == "use_skill")
            .Select(detail => detail.GetProperty("Detail").GetString()!).ToArray();
        That(skillDetails.Length == 8 &&
            skillDetails.Count(detail => detail.EndsWith("checksum=matched", StringComparison.Ordinal)) == 4 &&
            skillDetails.Count(detail => detail.EndsWith("checksum=mismatched", StringComparison.Ordinal)) == 2 &&
            skillDetails.Count(detail => detail.EndsWith("checksum=absent", StringComparison.Ordinal)) == 2,
            "真实TCP记录正常、篡改与10B摘要状态，未知宽度保持未实现");
    }
}
