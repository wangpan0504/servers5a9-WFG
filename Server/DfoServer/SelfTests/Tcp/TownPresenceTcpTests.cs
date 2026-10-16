using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class TownPresenceTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "presence"), gm: true, resources: resources);
        await fixture.StartAsync();
        using (var alpha = Open(fixture, "alpha"))
        using (var beta = Open(fixture, "beta"))
        {
            alpha.Create("PresenceA");
            beta.Create("PresenceB");
            alpha.Select();
            beta.Select();
            alpha.Barrier();
            beta.Barrier();
            alpha.Send(1426);
            alpha.SendBarrier();
            alpha.Batch((0, 23), (0, 24), (1, 1347));
            beta.Send(1426);
            beta.SendBarrier();
            var bEntry = beta.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
            alpha.SendBarrier();
            var aEntry = alpha.Batch((0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
            That(aEntry[0].Body[0] == 0 && U16(aEntry[0].Body, 121) == beta.Id && bEntry[2].Body[0] == 0 && U16(bEntry[2].Body, 121) == alpha.Id, "mutual public actor snapshots");
            Bytes(aEntry[2].Body, Build(w => { w.Write(beta.Id); w.Write((byte)38); w.Write((byte)0);
                w.Write((ushort)622); w.Write((ushort)196); w.Write((byte)0); w.Write((byte)3); }),
                "远端23携带同一地图与显示位，创建实体而不切换接收者地图");
            Bytes(aEntry[3].Body, Position(beta.Id, 622, 196), "peer entrance position");
            string selectionBefore = fixture.Snapshot();
            alpha.Send(15, new byte[4]);
            var select = alpha.Batch((0, 6), (1, 15), (0, 3), (0, 1536), (0, 27));
            Bytes(select[2].Body, [1, (byte)alpha.Id, (byte)(alpha.Id >> 8), 1], "选图本人从Town状态0变为State1");
            beta.SendBarrier();
            beta.Expect(0, 6, Build(w => w.Write(alpha.Id)));
            beta.Expect(1, 1347, [1]);
            alpha.Send(132);
            alpha.SendBarrier();
            alpha.Batch((1, 132), (0, 3), (0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
            beta.SendBarrier();
            beta.Batch((0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
            That(fixture.Snapshot() == selectionBefore, "选图离场和回城重建双向同屏，不改变两账号资产");
            alpha.Send(35, Move(633, 196));
            alpha.Barrier();
            beta.SendBarrier();
            beta.Expect(0, 22, Position(alpha.Id, 633, 196));
            beta.Expect(1, 1347, [1]);
            alpha.Send(35, Move(633, 196));
            alpha.Send(35, Move(0, 0));
            alpha.Barrier();
            beta.Barrier();
            alpha.Send(36, Area(38, 1, 450, 234, 38, 0));
            alpha.SendBarrier();
            alpha.Batch((0, 6), (0, 23), (0, 24), (0, 1379), (1, 1347));
            beta.SendBarrier();
            beta.Expect(0, 6, Build(w => w.Write(alpha.Id)));
            beta.Expect(1, 1347, [1]);
            alpha.Send(1426);
            alpha.SendBarrier();
            alpha.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
            beta.SendBarrier();
            beta.Batch((0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
            using (var replacement = Open(fixture, "alpha"))
            {
                alpha.Closed();
                beta.SendBarrier();
                beta.Expect(0, 6, Build(w => w.Write(alpha.Id)));
                beta.Expect(1, 1347, [1]);
                replacement.Select();
                // 旧连接 finally 已结束后，GM 必须仍找到新会话并原地更新其 Selected。
                string gm = await fixture.CliAsync(null, "--gm", "account", "alpha", "level", "PresenceA", "40");
                That(gm.StartsWith("OK ", StringComparison.Ordinal), "GM uses online replacement session");
                replacement.Send(35, Move(461, 234));
                replacement.Barrier();
                That(fixture.Snapshot("SELECT level,x FROM characters WHERE account='alpha'") == "[[40,461]]", "movement retains GM update after takeover");
                replacement.Send(1426);
                replacement.SendBarrier();
                replacement.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
                beta.SendBarrier();
                beta.Batch((0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
                replacement.Send(7);
                replacement.SendBarrier();
                replacement.Batch((0, 6), (1, 7), (0, 2));
                replacement.Send(8, [255, 255, 2]);
                beta.SendBarrier();
                beta.Expect(0, 6, Build(w => w.Write(alpha.Id)));
                beta.Expect(1, 1347, [1]);
            }

            beta.Send(3, [0]);
            beta.Expect(1, 3, [1]);
            beta.Closed();
        }

        await fixture.StopAsync();
        Console.WriteLine("PASS C# TCP presence: private/public scenes, snapshots, movement, scene clear, takeover, online GM and return selection");
    }

    static byte[] Position(ushort id, ushort x, ushort y) => Build(w =>
    {
        w.Write(id);
        w.Write(x);
        w.Write(y);
        w.Write((byte)0);
        w.Write((ushort)100);
    });
}
