using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class RaidChannelLandingTcpTests
{
    public static async Task RunReloginAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "raid-channel-relogin"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        var character = CreateRaider(fixture, resources, "RaidRelog");
        byte ordinary = endpoints.Keys.Min();
        foreach (byte channel in new byte[] { 19, 20, 39, 42 })
        {
            using (var entering = Open(fixture, gamePort: endpoints[channel], channelNumber: channel))
            {
                // 从普通频道主动换入时原生直发4；不冒充新登录先8再4。
                entering.Select(character.Slot, prepared: true, nativeSwitch: true);
                entering.Barrier();
                if (channel == 20)
                {
                    entering.Send(7, []);
                    entering.Expect(1, 7, [1]);
                    entering.Expect(0, 2);
                    entering.Select(character.Slot, prepared: true, nativeSwitch: false);
                    entering.Barrier();
                    That(entering.LoginEndpoint[3] == 32,
                        "本连接已真实入城后返回选角，仍可在原卢克频道选择角色");
                }
            }
            // 保存真实团本专属地图的遗留位置，普通返回地区保持原五字段。
            var stale = channel switch { 19 => (19, 2), 20 => (32, 4), 39 => (57, 2), _ => (59, 2) };
            fixture.Sql("UPDATE characters SET town=$p1,area=$p2 WHERE id=$p0", character.Id, stale.Item1, stale.Item2);
            string before = fixture.Snapshot();
            using (var fresh = Open(fixture, gamePort: endpoints[channel], channelNumber: channel))
            {
                fresh.Send(8, [255, 255, 2]);
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    fresh.Send(4, Build(writer => writer.Write(character.Slot)));
                    fresh.Expect(1, 4, [0, 96, 0]);
                    fresh.Expect(0, 936, []);
                    // 仍在选角阶段，使用可用的只读名册请求作屏障，不能发送入城后的1347。
                    fresh.Send(8, [255, 255, 2]);
                    fresh.Expect(0, 2);
                    That(fixture.Snapshot() == before,
                        "团本新登录只发原生选频道请求，不保存位置、不扣票、不初始化入城资产；重复不能绕过");
                }
            }
            using (var normal = Open(fixture, gamePort: endpoints[ordinary], channelNumber: ordinary))
            {
                normal.Select(character.Slot, prepared: true);
                normal.Barrier();
                That(normal.LoginEndpoint.Length > 3 && normal.LoginEndpoint[3] is not (23 or 32 or 45 or 50) &&
                    normal.SavedTownPosition() is { Town: 38, Area: 1 },
                    "重新选择普通频道使用真实普通监听端点，旧团本地图被正式选角清理到共享房间");
                That(fixture.Query("SELECT return_town,return_area FROM characters WHERE id=$p0", character.Id) is [[38L, 0L]],
                    "清理旧团本地图保留原普通返回地区");
            }
        }
    }
}
