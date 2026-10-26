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
        await VerifyConnectionAddressesAsync(source, root, resources);
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
            That(U32(hello, 0) == hello.Length - 4 && hello.AsSpan(343, 4).SequenceEqual(new byte[] { 0x38, 2, 0x40, number }),
                "所有分类入口首包均安装实际连接的频道身份，不固定回写10");
        }

        using var alpha = Open(fixture, "entrya", gamePort: endpoints[11], channelNumber: 11);
        using var isolated = Open(fixture, "entryb", gamePort: endpoints[12], channelNumber: 12);
        int nativePort = NativePort(fixture);
        var channelCatalog = DfoServer.Infrastructure.ServerRuntime.LoadChannels(Path.Combine(AppContext.BaseDirectory, "Resources", "channel.txt"));
        That(alpha.LoginEndpoint[3] == channelCatalog.Entry(11).Type &&
            isolated.LoginEndpoint[3] == channelCatalog.Entry(12).Type,
            "LOGIN第3偏移安装实际入口频道类型，使客户端冒险团准入与频道目录一致");
        That(U32(alpha.LoginEndpoint, 13 + checked((int)U32(alpha.LoginEndpoint, 9))) == nativePort &&
            U32(isolated.LoginEndpoint, 13 + checked((int)U32(isolated.LoginEndpoint, 9))) == nativePort &&
            !endpoints.Values.Contains(nativePort) && U32(alpha.LoginEndpoint, 5) != 0 && U32(isolated.LoginEndpoint, 5) != 0,
            "LOGIN返回独立Native端口及关联值，NOTI1和后续Actor仍使用所选频道身份");
        alpha.Create("EntryA"); isolated.Create("EntryB");
        var alphaEntry = alpha.Select();
        var isolatedEntry = isolated.Select();
        foreach (var frames in new[] { alphaEntry, isolatedEntry })
        {
            var ownActor = frames.Single(frame => frame is { Kind: 0, Opcode: 2 } && frame.Body[0] == 0).Body;
            That(ownActor[^15] == 2 && ownActor[^15] == ownActor[3] && ownActor[^9] == 0xFF,
                "实际TCP本人Actor携带非空本区身份供TAG候选使用，不把频道11/12或FF当大区");
        }
        alpha.Barrier(); isolated.Barrier();
        alpha.Send(1426); alpha.SendBarrier(); alpha.Batch((0, 23), (0, 24), (1, 1347));
        isolated.Send(1426); isolated.SendBarrier(); isolated.Batch((0, 23), (0, 24), (1, 1347));
        alpha.Barrier();
        alpha.Send(35, Move(633, 196)); alpha.Barrier(); isolated.Barrier();
        That(fixture.Query("SELECT id,name FROM characters ORDER BY id") is [[_, "EntryA"], [_, "EntryB"]],
            "不同频道城镇不互发入场和移动，但仍共用角色库及账号归属");
        alpha.Send(10, Build(w => { w.Write(isolated.Id); w.Write((byte)15); w.Write(new byte[10]); }));
        alpha.Barrier();
        alpha.Send(8, Build(w => { w.Write(isolated.Id); w.Write((byte)3); }));
        var remoteActor = alpha.ExpectOtherPlayerActor(isolated.Id, 12).Body;
        var remoteInfo = alpha.Expect(0, 2).Body;
        That(remoteActor[0] == 0 && remoteActor[4] == 12 && remoteActor[^15] == 2 && U16(remoteActor, 121) == isolated.Id &&
            remoteInfo[0] == 3 && remoteInfo[4] == 12 && U16(remoteInfo, 14) == isolated.Id,
            "跨频道资料使用目标频道缓存，右键peer请求不假装同频道目标");
        alpha.Barrier();
        isolated.Barrier();
        isolated.Send(3, [1]); isolated.Expect(1, 3, [1]); isolated.Closed();
        using var beta = Open(fixture, "entryb", gamePort: endpoints[11], channelNumber: 11);
        beta.Select(resumeTown: true, room: false);
        beta.SendBarrier(); var toBeta = beta.Batch((0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
        alpha.SendBarrier(); var toAlpha = alpha.Batch((0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
        That(U16(toAlpha[0].Body, 121) == beta.Id && U16(toBeta[0].Body, 121) == alpha.Id &&
            toAlpha[0].Body[4] == 11 && toBeta[0].Body[4] == 11 && toAlpha[0].Body[^15] == 2 && toBeta[0].Body[^15] == 2,
            "换入相同频道后双方收到完整同屏入场，Actor归当前频道上下文");
        alpha.Send(35, Move(634, 196)); alpha.Barrier();
        beta.SendBarrier(); beta.Batch((0, 22), (1, 1347));
        That(fixture.Query("SELECT id,name FROM characters ORDER BY id") is [[_, "EntryA"], [_, "EntryB"]],
            "各入口共用角色库，移动与账号归属不分片");

        ushort id = alpha.Id;
        alpha.Send(36, Area(38, 2, 1085, 249, 38, 0)); alpha.SendBarrier();
        alpha.Batch((0, 23), (0, 24), (1, 1347));
        beta.SendBarrier();
        beta.Expect(0, 23, Build(w => { w.Write(alpha.Id); w.Write(new byte[] { 38, 2, 61, 4, 249, 0, 0, 3 }); }));
        beta.Expect(1, 1347, [1]);
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
        byte[] hotkeys = "I01\t77\nC00\t134\n"u8.ToArray();
        byte[] hotkeyRequest = Build(writer => { writer.Write(hotkeys.Length); writer.Write(hotkeys); });
        (ushort Opcode, byte[] Body)[] settings = [(390, keyRestore), (454, [0, .. hotkeyRequest])];
        alpha.Send(198, hotkeyRequest);
        alpha.Send(447, keyRequest);
        alpha.Barrier();
        string location = fixture.Snapshot("SELECT town,area,x,y,direction,revision,return_town,return_area FROM characters WHERE id=$p0", id);
        alpha.Send(3, [1]);
        alpha.Expect(1, 3, [1]); alpha.Closed();
        using var switched = Open(fixture, "entrya", gamePort: endpoints[21], channelNumber: 21);
        var reentry = switched.Select(resumeTown: true, room: false, settings: settings);
        That(switched.ClientSettings is [{ Kind: 0, Opcode: 390 }, { Kind: 0, Opcode: 454 }], "换频道的设置回放在最终24绑定后逐包核对，保留角色选项与账号键位");
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
        switched.Batch((0, 23), (0, 24), (1, 1347));
        beta.Barrier();
        That(switched.Id == id, "换频道回到相同城镇仍保留角色，但不向旧频道重新发送同屏入场");

        using var takeover = Open(fixture, "entrya");
        switched.Closed(); beta.Barrier();
        takeover.Select(settings: settings); takeover.Barrier();
        That(takeover.Id == id, "跨入口接管沿用唯一账号generation，旧连接清理不删除新owner");
        That(fixture.Snapshot("SELECT town,area,x,y FROM characters WHERE id=$p0", id) == "[[38,1,450,234]]",
            "主入口格兰之森普通名册选角与换频道分开，冷登录仍回共享房间");
        takeover.Send(35, Move(451, 235)); takeover.Barrier();
        string gateLocation = fixture.Snapshot("SELECT town,area,x,y,revision FROM characters WHERE id=$p0", id);
        takeover.Send(3, [1]); takeover.Expect(1, 3, [1]); takeover.Closed();
        using var gateReentry = Open(fixture, "entrya", gamePort: endpoints[11], channelNumber: 11);
        var gatePackets = gateReentry.Select(resumeTown: true, settings: settings);
        gateReentry.Barrier();
        That(gatePackets[^2] is { Kind: 0, Opcode: 124 } && gatePackets[^1] is { Kind: 0, Opcode: 24 } &&
            fixture.Snapshot("SELECT town,area,x,y,revision FROM characters WHERE id=$p0", id) == gateLocation,
            "gate换频道沿用合法1379与124/24边界，不回归为强制坐标重置");
    }

    static async Task VerifyConnectionAddressesAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "channel-address"), resources: resources, connectionAddress: true);
        await fixture.StartAsync();
        foreach (string address in new[] { "127.0.0.2", "127.0.0.1" })
            ChannelTcpTests.Run(fixture, address);
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        using var alpha = Open(fixture, "addressa", gamePort: endpoints[11], channelNumber: 11, serverAddress: "127.0.0.2");
        using var beta = Open(fixture, "addressb", gamePort: endpoints[12], channelNumber: 12);
        foreach (var (peer, address) in new[] { (alpha, "127.0.0.2"), (beta, "127.0.0.1") })
        {
            int length = checked((int)U32(peer.LoginEndpoint, 9));
            That(System.Text.Encoding.ASCII.GetString(peer.LoginEndpoint, 13, length) == address &&
                U32(peer.LoginEndpoint, 13 + length) == NativePort(fixture),
                "并发原生登录返回各自连接抵达的服务端IP和同实例Native端口，不污染其他连接");
        }
        That(fixture.Options.AdvertisedAddress == "connection", "目录与登录端点不改写共享地址策略");
    }

    static int NativePort(TcpTestFixture fixture)
    {
        using var ready = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "ready.json")));
        return ready.RootElement.GetProperty("NativePort").GetInt32();
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
