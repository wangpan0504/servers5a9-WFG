using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class TownPresenceTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "presence"), gm: true, resources: resources);
        await fixture.StartAsync();
        That((await fixture.CliAsync(null, "--gm", "pc-room", "on")) == "OK {\"premiumPcRoom\":true}",
            "新建会话前全局开启独立网吧身份");
        using (var alpha = Open(fixture, "alpha"))
        using (var beta = Open(fixture, "beta"))
        {
            alpha.Create("PresenceA");
            beta.Create("PresenceB", job: 1);
            AssertBlackDiamondActor(alpha.Select()[2].Body, active: false, pcRoom: true);
            AssertBlackDiamondActor(beta.Select()[2].Body, active: false, pcRoom: true);
            alpha.Barrier();
            beta.Barrier();
            string beforePcRoom = fixture.Snapshot() + fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account");
            That((await fixture.CliAsync(null, "--gm", "pc-room", "off")) == "OK {\"premiumPcRoom\":false}",
                "全局关闭网吧身份而不是按账号撤销黑钻");
            alpha.CheckPcRoomRefresh(false, blackDiamond: false);
            beta.CheckPcRoomRefresh(false, blackDiamond: false);
            That(fixture.Snapshot() + fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") == beforePcRoom,
                "同一全局开关作用于两个账号但不写任何角色资产或会员期限");
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
            string beforeInteraction = fixture.Snapshot() + fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account");
            alpha.Send(10, Build(w => { w.Write(beta.Id); w.Write((byte)15); w.Write(0u); w.Write((ushort)0); w.Write(0u); }));
            alpha.Expect(0, 7, Build(w => { w.Write(beta.Id); w.Write((byte)15); w.Write(0u); w.Write(ushort.MaxValue); }));
            alpha.Barrier();
            beta.Barrier();
            alpha.Send(8, Build(w => { w.Write(beta.Id); w.Write((byte)3); }));
            var publicActor = alpha.Expect(0, 2).Body;
            var publicInfo = alpha.Expect(0, 2).Body;
            That(publicActor[0] == 0 && U16(publicActor, 121) == beta.Id &&
                publicInfo[0] == 3 && U16(publicInfo, 1) == 1 && U16(publicInfo, 14) == beta.Id,
                "查看资料先安装目标身份再发subtype3，不把目标覆盖到本人subtype1");
            That(U32(publicInfo, 24) == 89 && publicInfo.AsSpan(28, 89).SequenceEqual(beta.PlayerBody.AsSpan(36, 89)) &&
                !publicInfo.AsSpan(28, 89).SequenceEqual(alpha.PlayerBody.AsSpan(36, 89)),
                "不同职业的双方查询使用目标属性，不泄漏或误用请求者资产");
            alpha.Barrier();
            beta.Barrier();
            alpha.Send(10, Build(w => { w.Write(ushort.MaxValue); w.Write((byte)15); w.Write(new byte[10]); }));
            alpha.Send(8, [255, 255, 3]);
            alpha.Send(10, Build(w => { w.Write(beta.Id); w.Write((byte)2); w.Write(new byte[8]); }));
            alpha.Barrier();
            That(fixture.Snapshot() + fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") == beforeInteraction,
                "菜单、公开查询、离线目标与其他未实现peer模式均不修改双方资产或假回成功");
            beta.Send(12, Convert.FromHexString("000304000000000500000000000007"));
            var party = beta.Batch((0, 2), (0, 153), (0, 11), (0, 9), (0, 11), (0, 153), (0, 26))[3].Body;
            ushort partyId = U16(party, 4);
            beta.Barrier();
            Bytes(alpha.Expect(0, 9).Body, party, "同屏观察者收到新建队伍的相同设置与名册");
            alpha.Send(10, Build(w => { w.Write(beta.Id); w.Write((byte)15); w.Write(new byte[10]); }));
            alpha.Expect(0, 7, Build(w => { w.Write(beta.Id); w.Write((byte)15); w.Write(0u); w.Write(partyId); }));
            beta.Send(13); beta.Expect(1, 13, [1]); beta.Expect(0, 9); beta.Barrier();
            alpha.Expect(0, 9);
            alpha.Send(10, Build(w => { w.Write(beta.Id); w.Write((byte)15); w.Write(new byte[10]); }));
            alpha.Expect(0, 7, Build(w => { w.Write(beta.Id); w.Write((byte)15); w.Write(0u); w.Write(ushort.MaxValue); }));
            alpha.Barrier();
            string otherBefore = fixture.Snapshot("SELECT * FROM account_wallets WHERE account='beta'") +
                fixture.Snapshot("SELECT * FROM characters WHERE account='beta'");
            foreach (bool active in new[] { true, false })
            {
                string reply = await fixture.CliAsync(null, "--gm", "account", "alpha", "black-diamond", "PresenceA", active ? "30" : "0");
                That(reply.StartsWith("OK ", StringComparison.Ordinal), "同屏场景按账号开通或撤销黑钻");
                alpha.CheckBlackDiamondRefresh(active);
                beta.SendBarrier();
                var refreshed = beta.Expect(0, 2).Body;
                That(U16(refreshed, 121) == alpha.Id, "黑钻刷新只替换原同屏成员，不更换成员身份或场景");
                AssertBlackDiamondActor(refreshed, active);
                beta.Expect(1, 1347, [1]);
                beta.Barrier();
                That(fixture.Snapshot("SELECT * FROM account_wallets WHERE account='beta'") +
                    fixture.Snapshot("SELECT * FROM characters WHERE account='beta'") == otherBefore,
                    "同屏观察不把源账号黑钻或PC房资格写入接收账号");
            }
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
            alpha.Batch((0, 23), (0, 24), (0, 1379), (1, 1347));
            beta.SendBarrier();
            beta.Expect(0, 23, Build(w => { w.Write(alpha.Id); w.Write(new byte[] { 38, 1, 194, 1, 234, 0, 0, 3 }); }));
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
