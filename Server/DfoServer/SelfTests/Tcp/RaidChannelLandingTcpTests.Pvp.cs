using DfoServer.Game.Characters;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class RaidChannelLandingTcpTests
{
    static async Task PvpLandingAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "pvp-town-landing"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        var character = CreateRaider(fixture, resources, "PvpLanding");
        var change = Switcher(fixture, resources, endpoints, character);
        string Back() => fixture.Snapshot("SELECT return_town,return_area FROM characters WHERE id=$p0", character.Id);
        string original = Back();
        foreach (byte channel in new byte[] { 101, 47, 51, 55 })
        {
            change(channel, true, 10, $"普通→决斗{channel}，直接进入专属城镇");
            That(Back() == original, $"决斗入口保留普通城镇返回地区：{original}，实际{Back()}");
            change(channel, false, null, $"决斗{channel}同频道重连保留决斗地区及可移动位置");
            using (var peer = Open(fixture, gamePort: endpoints[channel], channelNumber: channel))
            {
                var packets = peer.Select(character.Slot, prepared: true, room: false);
                var area = packets.Single(packet => packet is { Kind: 0, Opcode: 24 }).Body;
                That(area[0] == 10 && area[1] == 0 && U16(area, 6) == 474 && U16(area, 8) == 234,
                    "决斗频道普通登录选角按资源锚点10/0、474/234入城");
                peer.Barrier();
                peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();
            }
            change(10, true, 38, "决斗→普通，恢复共享房间", area: 1, exit: 38);
            That(Back() == original, "退出决斗频道后仍回原普通地区");
        }
        change(101, true, 10, "普通进入传统后连续切换决斗频道");
        foreach (byte channel in new byte[] { 47, 51, 55, 101 })
        {
            change(channel, false, null, $"决斗频道互换至{channel}保持城镇快照并切换真实频道身份");
            That(Back() == original, "决斗频道互换也保留普通返回地区");
        }
    }
}
