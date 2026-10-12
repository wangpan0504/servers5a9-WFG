using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;
using System.Net;
using System.Net.Sockets;
using DfoServer.GameWorld.Channels;
using DfoServer.Network;

namespace DfoServer.Testing;

internal static class ChannelEntryTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        VerifyListenerRollback();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "channel-entry"), resources: resources);
        await fixture.StartAsync();
        ChannelTcpTests.Run(fixture);
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        foreach (var (number, port) in endpoints)
        {
            using var probe = new TcpPeer(port, channelNumber: number);
            var hello = probe.Expect(0, 1).Body;
            for (int i = 0; i < hello.Length; i++)
                hello[i] = (byte)((((hello[i] << 6) | (hello[i] >> 2)) ^ 0xb5) & 0xff);
            That(hello.Length == 357 && hello.AsSpan(343, 4).SequenceEqual(new byte[] { 0x38, 2, 0x40, number }),
                "所有分类入口首包均安装实际连接的频道身份，不固定回写10");
        }

        using var alpha = Open(fixture, "entrya", gamePort: endpoints[11], channelNumber: 11);
        using var beta = Open(fixture, "entryb", gamePort: endpoints[12], channelNumber: 12);
        That(U32(alpha.LoginEndpoint, 13 + checked((int)U32(alpha.LoginEndpoint, 9))) == endpoints[11] &&
            U32(beta.LoginEndpoint, 13 + checked((int)U32(beta.LoginEndpoint, 9))) == endpoints[12],
            "LOGIN端点与本连接NOTI1和实际入口相同，不把分类重新指向主端口");
        alpha.Create("EntryA"); beta.Create("EntryB");
        alpha.Select(); beta.Select(); alpha.Barrier(); beta.Barrier();
        alpha.Send(1426); alpha.SendBarrier(); alpha.Batch((0, 23), (0, 24), (1, 1347));
        beta.Send(1426); beta.SendBarrier();
        var toBeta = beta.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 22), (1, 1347));
        alpha.SendBarrier(); var toAlpha = alpha.Batch((0, 2), (0, 3), (0, 22), (1, 1347));
        That(U16(toAlpha[0].Body, 121) == beta.Id && U16(toBeta[2].Body, 121) == alpha.Id &&
            toAlpha[0].Body[4] == 11 && toBeta[2].Body[4] == 12,
            "不同分类不隔离同屏；双方Actor均归接收者本地上下文，不落入跨频道缓存");
        alpha.Send(35, Move(633, 196)); alpha.Barrier();
        beta.SendBarrier(); beta.Batch((0, 22), (1, 1347));
        That(fixture.Query("SELECT id,name FROM characters ORDER BY id") is [[_, "EntryA"], [_, "EntryB"]],
            "各入口共用角色库，移动与账号归属不分片");

        ushort id = alpha.Id;
        alpha.Send(36, Area(38, 2, 1085, 249, 38, 0)); alpha.SendBarrier();
        alpha.Batch((0, 6), (0, 23), (0, 24), (1, 1347));
        beta.SendBarrier(); beta.Batch((0, 6), (1, 1347));
        byte[] keys = new byte[512];
        keys[0] = 7;
        byte[] keyRequest = Build(writer =>
        {
            writer.Write(keys.Length);
            writer.Write(keys);
        });
        byte[] keyRestore = Build(writer =>
        {
            writer.Write(keys.Length);
            writer.Write(keys);
        });
        alpha.Send(447, keyRequest);
        alpha.Barrier();
        string location = fixture.Snapshot("SELECT town,area,x,y,direction,revision,return_town,return_area FROM characters WHERE id=$p0", id);
        alpha.Send(3, [1]);
        alpha.Expect(1, 3, [1]); alpha.Closed();
        using var switched = Open(fixture, "entrya", gamePort: endpoints[21], channelNumber: 21);
        var reentry = switched.Select(resumeTown: true, room: false, settings: [(390, keyRestore)]);
        That(switched.ClientSettings is [{ Kind: 0, Opcode: 390 }], "换频道的设置回放在最终24绑定后逐包核对，不丢失角色选项");
        That(switched.Id == id && fixture.Query("SELECT count(*) FROM characters WHERE account='entrya'") is [[1L]] &&
            fixture.Snapshot("SELECT town,area,x,y,direction,revision,return_town,return_area FROM characters WHERE id=$p0", id) == location,
            "非gate原生换频道保留完整位置与revision，不建第二份角色或重新发放资产");
        Bytes(reentry.Single(p => p is { Kind: 0, Opcode: 23 }).Body,
            Build(writer =>
            {
                writer.Write(id);
                writer.Write(new byte[] { 38, 2 });
                writer.Write((ushort)1085);
                writer.Write((ushort)249);
                writer.Write(new byte[] { 0, 3 });
            }),
            "区域通知保留非gate地图、坐标及原转职显示标志，不能强制回旅馆");
        That(reentry[^2] is { Kind: 0, Opcode: 124 } && reentry[^1] is { Kind: 0, Opcode: 24 } &&
            U16(reentry[^1].Body, 4) == id && reentry[^1].Body[0] == 38 && reentry[^1].Body[1] == 2,
            "入城完成的原生场景恢复后才绑定最终AREA_USERS本人记录");
        switched.Send(36, Area(38, 2, 1085, 249, 38, 2)); switched.Barrier();
        That(fixture.Snapshot("SELECT town,area,x,y,direction,revision,return_town,return_area FROM characters WHERE id=$p0", id) == location,
            "客户端重连位置请求回放属于same-area零变化，不按旅馆出口误拒绝或重复写入");
        switched.Send(35, Move(949, 267)); switched.Barrier();
        That(fixture.Snapshot("SELECT town,area,x,y FROM characters WHERE id=$p0", id) == "[[38,2,949,267]]",
            "非gate换频道后可以继续在原图移动");
        switched.Send(36, Area(38, 0, 629, 196, 38, 2)); switched.SendBarrier();
        switched.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 22), (1, 1347));
        beta.SendBarrier(); var afterSwitch = beta.Batch((0, 2), (0, 3), (0, 22), (1, 1347));
        That(afterSwitch[0].Body[4] == 12 && U16(afterSwitch[0].Body, 121) == id,
            "换入口后仍回到同一公共场景，新频道身份不改变对方本地缓存键");

        using var takeover = Open(fixture, "entrya");
        switched.Closed(); beta.SendBarrier(); beta.Batch((0, 6), (1, 1347));
        takeover.Select(settings: [(390, keyRestore)]); takeover.Barrier();
        That(takeover.Id == id, "跨入口接管沿用唯一账号generation，旧连接清理不删除新owner");
        That(fixture.Snapshot("SELECT town,area,x,y FROM characters WHERE id=$p0", id) == "[[38,1,450,234]]",
            "普通名册选角与换频道分开，冷登录仍回map gate");
        takeover.Send(35, Move(451, 235)); takeover.Barrier();
        string gateLocation = fixture.Snapshot("SELECT town,area,x,y,revision FROM characters WHERE id=$p0", id);
        takeover.Send(3, [1]); takeover.Expect(1, 3, [1]); takeover.Closed();
        using var gateReentry = Open(fixture, "entrya", gamePort: endpoints[11], channelNumber: 11);
        var gatePackets = gateReentry.Select(resumeTown: true, settings: [(390, keyRestore)]);
        gateReentry.Barrier();
        That(gatePackets[^2] is { Kind: 0, Opcode: 124 } && gatePackets[^1] is { Kind: 0, Opcode: 24 } &&
            fixture.Snapshot("SELECT town,area,x,y,revision FROM characters WHERE id=$p0", id) == gateLocation,
            "gate换频道沿用合法1379与124/24边界，不回归为强制坐标重置");
    }

    static void VerifyListenerRollback()
    {
        const string row = "`<chn_channel_info_028>` 1 `[granfloris]` 5 0 0 0 0 0 0 0 0 0 0 ``";
        var catalog = ChannelCatalog.Parse("[server]\n2\n10 " + row + "\n11 " + row + "\n[/server]\n");
        var primary = new TcpListener(IPAddress.Loopback, 0);
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        primary.Start(); occupied.Start();
        int primaryPort = ((IPEndPoint)primary.LocalEndpoint).Port;
        int aliasPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var listeners = new GameChannelListeners(new() { GamePort = primaryPort, ChannelEntryPortBase = aliasPort }, catalog);
        try
        {
            primary.Stop();
            Reject<SocketException>(listeners.Start, "附加入口被占用时拒绝整组启动");
            That(listeners.Bound.Count == 0 && listeners.Endpoints.Count == 0, "入口绑定失败关闭已经启动的主入口，不发布半份目录");
            occupied.Stop();
            listeners.Start();
            That(listeners.Endpoints.Select(row => row.Port).SequenceEqual(new[] { primaryPort, aliasPort }),
                "失败后可恢复同一固定端口计划，主入口与附加入口没有残留占用");
        }
        finally
        {
            listeners.Stop(); primary.Stop(); occupied.Stop();
        }
    }
}
