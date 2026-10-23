using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class AdventureWeeklyTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DfoServer.Development.Dungeons.DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "adventure-weekly"), resources: resources);
        await fixture.StartAsync();
        using (var peer = Open(fixture, "advweekly"))
        {
            peer.Create("WeeklyA");
            peer.Create("WeeklyB");
            // 选角期名册查询：718不依赖已就绪角色。
            peer.Send(789, [0]);
            That(ReadList(peer.Expect(0, 718).Body, out var before) && before == "1:WeeklyA|2:WeeklyB",
                "选角期789返回本账号两个角色及服务器号");
            var infoBefore = peer.Expect(0, 719).Body;
            That(infoBefore.Length >= 2 && infoBefore[0] == 0 && infoBefore[1] == 2,
                "789随后整份回719，key用请求服务器号并带本账号两行");
            // 未就绪时拒绝保存配置，不能用通用成功冒充已保存。
            peer.Send(791, [1, 2, 3, 4, 0xFF]); peer.Expect(1, 791, [0, 3, 0]);
            That(fixture.Query("SELECT count(*) FROM account_adventure_weekly_config") is [[0L]], "选角期拒绝791且不建配置行");
            peer.Select();
            That(peer.WeeklyDungeonConfig.SequenceEqual(new byte[] { 0, 1, 3, 4, 6 }),
                "首次入城无配置行时按PVF首周默认排列[0 1 3 4 6]回放");
            peer.Send(789, [0]);
            That(ReadList(peer.Expect(0, 718).Body, out var ready) && ready == "1:WeeklyA|2:WeeklyB",
                "入城后789仍返回同一名册");
            peer.Expect(0, 719);
            peer.Send(791, [1, 2, 3, 4, 0xFF]); peer.Expect(1, 791, [1]);
            peer.Expect(0, 721, [1, 2, 3, 4, 0xFF]);
            That(fixture.Query("SELECT hex(slots) FROM account_adventure_weekly_config") is [["01020304FF"]],
                "791按客户端提交的5槽位原样保存并回放721");
            peer.Send(791, [0, 1, 2, 3, 0xFF]); peer.Expect(1, 791, [1]);
            peer.Expect(0, 721, [0, 1, 2, 3, 0xFF]);
            That(fixture.Query("SELECT hex(slots) FROM account_adventure_weekly_config") is [["00010203FF"]] &&
                fixture.Query("SELECT count(*) FROM account_adventure_weekly_config") is [[1L]] &&
                fixture.Query("SELECT revision FROM account_adventures") is [[0L]],
                "重复保存覆盖同一账号行，不推进账号revision");
            peer.Barrier();
        }
        await fixture.StopAsync();
        await fixture.StartAsync("weekly-restarted", isolatedProcess: true);
        using (var peer = Open(fixture, "advweekly"))
        {
            peer.Select();
            That(peer.WeeklyDungeonConfig.SequenceEqual(new byte[] { 0, 1, 2, 3, 0xFF }),
                "跨进程重启后就绪阶段回放已保存排列");
            peer.Send(789, [0]);
            That(ReadList(peer.Expect(0, 718).Body, out var restarted) && restarted == "1:WeeklyA|2:WeeklyB",
                "跨进程重启后718仍按名册槽位返回");
            peer.Expect(0, 719);
            That(fixture.Query("SELECT hex(slots) FROM account_adventure_weekly_config") is [["00010203FF"]],
                "跨进程保留已提交的每周地下城排列");
            // 显式清空：保存成功后先720清列表，再回放全未选择的721。
            peer.Send(791, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF]); peer.Expect(1, 791, [1]);
            peer.Expect(0, 720, []);
            peer.Expect(0, 721, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
            That(fixture.Query("SELECT hex(slots) FROM account_adventure_weekly_config") is [["FFFFFFFFFF"]],
                "显式清空保存全FF并原样回放");
        }
        // 4字节配置是结构错误：连接按Parser边界关闭，不能被当成5槽位截断或伪造成功。
        using (var peer = Open(fixture, "advweekly"))
        {
            peer.Select();
            peer.Send(791, [1, 2, 3, 4]); peer.Closed();
        }
        Console.WriteLine($"PASS {Count - checks} 每周地下城信息789名册、791配置保存及跨进程恢复TCP断言");
        // 718布局：server u8、count u8，每行u32角色/u32名称长度/CP936名称/u8标志/u16等级/u8职业/u8分支/u8二觉。
        static bool ReadList(byte[] body, out string rows)
        {
            rows = "";
            if (body.Length < 2 || body[1] != 2) return false;
            var names = new List<string>();
            int at = 2;
            for (int i = 0; i < body[1]; i++)
            {
                if (at + 14 > body.Length) return false;
                uint id = BitConverter.ToUInt32(body, at);
                uint length = BitConverter.ToUInt32(body, at + 4);
                if (length > 29 || at + 14 + length > body.Length || body[at + 8 + length] != 0) return false;
                names.Add(id + ":" + DfoServer.Game.Characters.CharacterName.Encoding.GetString(body, at + 8, checked((int)length)));
                at += 14 + checked((int)length);
            }
            rows = string.Join('|', names);
            return at == body.Length && body[0] == 0;
        }
    }
}
